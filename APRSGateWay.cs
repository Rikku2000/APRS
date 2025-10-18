using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace APRSForwarder
{
    public class APRSGateWay
    {
        private string callsign = "13MAD86";
        private string passw = "-1";

        private TcpClient tcp_in_client = null;
        private string server_in = "127.0.0.1";
        private int port_in = 10152;

        private TcpClient tcp_out_client = null;
        private string server_out = "127.0.0.1";
        private int port_out = 14580;

        private Thread tcp_listen = null;
        private Hashtable timeoutedCmdList = new Hashtable();
        private string _state = "idle";
        private bool _active = false;

        public string State
        {
            get
            {
                return _state;
            }
        }

        public string lastRX = "";
        public string lastTX = "";

		/* MeshMqttBridge */
		private MeshMqttBridge _meshBridge;
		private bool _meshEnabled = false;
		/* MeshMqttBridge */

        public APRSGateWay()
		{
		}
       
        public void Start()
        {
			IniFile file = new IniFile("aprsgateway.ini");

			server_in = file.Read("server_in_url", "APRSGateway");
			port_in = Int32.Parse(file.Read("server_in_port", "APRSGateway"));
			server_out = file.Read("server_out_url", "APRSGateway");
			port_out = Int32.Parse(file.Read("server_out_port", "APRSGateway")); 
			callsign = file.Read("callsign", "APRSGateway");
			passw = file.Read("passcode", "APRSGateway");

			/* MeshMqttBridge */
			_meshEnabled = string.Equals(file.Read("enabled", "MeshGateway") ?? "false", "true", StringComparison.OrdinalIgnoreCase);
			if (_meshEnabled) {
				string host = file.Read("mqtt_host", "MeshGateway");
				int mport   = Int32.Parse(file.Read("mqtt_port", "MeshGateway"));
				bool tls    = string.Equals(file.Read("use_tls", "MeshGateway"), "true", StringComparison.OrdinalIgnoreCase);
				bool tlsIgn = string.Equals(file.Read("tls_ignore_errors", "MeshGateway"), "true", StringComparison.OrdinalIgnoreCase);
				string user = file.Read("mqtt_user", "MeshGateway");
				string pass = file.Read("mqtt_pass", "MeshGateway");
				string topic= file.Read("mqtt_topic", "MeshGateway");
				int ka      = Int32.Parse(file.Read("keepalive", "MeshGateway"));
				string pref = file.Read("node_callsign_prefix", "MeshGateway");
				string sym  = file.Read("default_symbol", "MeshGateway");
				string cmt  = file.Read("comment_suffix", "MeshGateway");
				int ts      = Int32.Parse(file.Read("threadsleep", "MeshGateway"));

				_meshBridge = new MeshMqttBridge(this, host, mport, tls, tlsIgn, user, pass, topic, ka, pref, sym, cmt, ts);
				_meshBridge.Start();
			}
			/* MeshMqttBridge */

            if (_active) return;
            lock (timeoutedCmdList) timeoutedCmdList.Clear();
            _active = true;
            _state = "starting";
            tcp_listen = new Thread(ReadIncomingDataThread);
            tcp_listen.Start();
            (new Thread(DelayedUpdate)).Start();
            _state = "started";
        }

        public void Stop()
        {
            if (!_active) return;
            lock (timeoutedCmdList) timeoutedCmdList.Clear();
            _state = "stopping";
            _active = false;

			/* MeshMqttBridge */
			if (_meshBridge != null) { try { _meshBridge.Stop(); } catch { } _meshBridge = null; }
			/* MeshMqttBridge */

            if (tcp_in_client != null)
            {
                tcp_in_client.Close();
                tcp_in_client = null;
            };
            _state = "stopped";
        }

        public bool Connected
        {
            get
            {
                if (!_active) return false;
                if (tcp_in_client == null) return false;
                return IsConnected(tcp_in_client);
            }
        }

        private static bool IsConnected(TcpClient Client)
        {
            if (!Client.Connected) return false;
            if (Client.Client.Poll(0, SelectMode.SelectRead))
            {
                byte[] buff = new byte[1];
                try
                {
                    if (Client.Client.Receive(buff, SocketFlags.Peek) == 0)
                        return false;
                }
                catch
                {
                    return false;
                };
            };
            return true;
        }

        private void ReadIncomingDataThread()
        {
            uint incomingMessagesCounter = 0;
            DateTime lastIncDT = DateTime.UtcNow;
            while (_active)
            {
                if ((tcp_in_client == null) || (!IsConnected(tcp_in_client)))
                {
                    tcp_in_client = new TcpClient();
                    try
                    {
                        _state = "connecting";
                        tcp_in_client.Connect(server_in, port_in);
                        string txt2send = "user " + callsign + " pass " + passw + " vers APRSGateWay 0.1\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_in_client.GetStream().Write(arr, 0, arr.Length);
                        incomingMessagesCounter = 0;
                        _state = "Connected";
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch
                    {
                        _state = "disconnected";
                        tcp_in_client.Close();
                        tcp_in_client = new TcpClient();
                        Thread.Sleep(5000);
                        continue;
                    };
                };

                if (DateTime.UtcNow.Subtract(lastIncDT).TotalMinutes > 3)
                {
                    try
                    {
                        string txt2send = "#ping\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_in_client.GetStream().Write(arr, 0, arr.Length);
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch { continue; };
                };                

                try
                {
                    byte[] data = new byte[65536];
                    int ava = 0;
                    if ((ava = tcp_in_client.Available) > 0)
                    {
                        lastIncDT = DateTime.UtcNow;
                        int rd = tcp_in_client.GetStream().Read(data, 0, ava > data.Length ? data.Length : ava);
                        string txt = System.Text.Encoding.GetEncoding(1251).GetString(data, 0, rd);
                        string[] lines = txt.Split(new string[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (string line in lines)
                            do_incoming(line, ++incomingMessagesCounter);
                    };
                }
                catch
                {
                    tcp_in_client.Close();
                    tcp_in_client = new TcpClient();
                    Thread.Sleep(5000);
                    continue;
                };
                

                Thread.Sleep(100);
            };
        }        

        private void do_incoming(string line, uint incomingMessagesCounter)
        {
            if (incomingMessagesCounter == 2)
            {
                if (line.IndexOf(" verified") > 0)
                    _state = "[APRS] Connected rx/tx, " + line.Substring(line.IndexOf("server"));
                if (line.IndexOf(" unverified") > 0)
                    _state = "[APRS] Connected rx only, " + line.Substring(line.IndexOf("server"));

				Console.WriteLine(_state);
            };

            bool isComment = line.IndexOf("#") == 0;
            if (!isComment)
            {
                lastRX = line;
				Console.WriteLine("[APRS] "+ lastRX);
				TCPSend (server_out, port_out, lastRX);
                onPacket(line);
            };
        }

        public delegate void onAPRSGWPacket(string line);
        public onAPRSGWPacket onPacket;

        public bool SendCommand(string cmd)
        {
            if (Connected)
            {
                lastTX = cmd;
				Console.WriteLine(lastTX);
                byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(cmd);
                try
                {
                    tcp_in_client.GetStream().Write(arr, 0, arr.Length);
                    return true;
                }
                catch
                {};
            };
            return false;
        }

        private void DelayedUpdate()
        {
            int timer = 0;
            while (_active)
            {
                timer++;
                if (timer == 60)
                {
                    timer = 0;
                    List<string> keys = new List<string>();
                    lock (timeoutedCmdList)
                    {
                        foreach (string key in timeoutedCmdList.Keys)
                            keys.Add(key);
                        foreach (string key in keys)
                        {
                            string cmd = (string)timeoutedCmdList[key];
                            timeoutedCmdList.Remove(key);
                            SendCommand(cmd);
                        };
                    };
                };
                Thread.Sleep(1000);
            };
        }

        public void TCPSend(string host, int port, string data)
        {
            try
            {
				tcp_out_client = new TcpClient();
				tcp_out_client.Connect(server_out, port_out);
				string txt2send = "user " + callsign + " pass " + passw + " vers APRSGateWay 0.1\r\n";
				byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
				tcp_out_client.GetStream().Write(arr, 0, arr.Length);
				Thread.Sleep(100);

                byte[] dt = System.Text.Encoding.GetEncoding(1251).GetBytes(data + "\r\n");
                try { tcp_out_client.GetStream().Write(dt, 0, dt.Length); } catch { };
				Thread.Sleep(100);
				tcp_out_client.Close();
            }
            catch (Exception ex) { throw ex; };
        }
    }
}
