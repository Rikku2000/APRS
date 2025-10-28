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
using System.Globalization;

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
		private int _reconnectDelayMs = 5000;

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
		/* FlightAwareBridge */
		private FlightAwareBridge _faBridge;
		private bool _faEnabled = false;
		/* FlightAwareBridge */
		/* Dump1090Bridge */
		private Dump1090Bridge _d1090;
		private bool _d1090Enabled = false;
		/* Dump1090Bridge */
		/* VesselFinderBridge */
		private VesselFinderBridge _vfBridge;
		private bool _vfEnabled = false;
		/* VesselFinderBridge */

        public APRSGateWay()
		{
		}

		private static readonly Regex UncompressedPos = new Regex(@"[!=@]\s*(\d{2})(\d{2}\.\d{2})([NS]).*?([0-1]\d{2})(\d{2}\.\d{2})([EW])", RegexOptions.Compiled);
		private static bool IsNullIslandAprs(string line)
		{
		   var m = UncompressedPos.Match(line);
		   if (!m.Success) return false;
		   int latDeg  = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
		   double latMin = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
		   int lonDeg  = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
		   double lonMin = double.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
		   return latDeg == 0 && Math.Abs(latMin) < 1e-6 &&
				  lonDeg == 0 && Math.Abs(lonMin) < 1e-6;
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

			/* FlightAware */
			_faEnabled = string.Equals(file.Read("enabled", "FlightAware") ?? "false", "true", StringComparison.OrdinalIgnoreCase);
			if (_faEnabled)
			{
				string url  = file.Read("aircraft_url", "FlightAware");
				int poll    = 5;
				int.TryParse(file.Read("poll_secs", "FlightAware"), out poll);
				string sym  = file.Read("default_symbol", "FlightAware");
				string cmt  = file.Read("comment_suffix", "FlightAware");
				string pref = file.Read("node_callsign_prefix", "FlightAware");

				_faBridge = new FlightAwareBridge(this, url, poll, sym, cmt, pref);
				_faBridge.Start();
			}
			/* FlightAware */

			/* Dump1090Bridge */
			_d1090Enabled = string.Equals(file.Read("enabled", "Dump1090") ?? "false", "true", StringComparison.OrdinalIgnoreCase);
			if (_d1090Enabled)
			{
				string jurl = file.Read("json_url", "Dump1090");
				int poll = 5; int.TryParse(file.Read("poll_secs", "Dump1090"), out poll);

				string sbsHost = file.Read("sbs_host", "Dump1090");
				int sbsPort = 30003; int.TryParse(file.Read("sbs_port", "Dump1090"), out sbsPort);

				string sym  = file.Read("default_symbol", "Dump1090");
				string cmt  = file.Read("comment_suffix", "Dump1090");
				string pref = file.Read("node_callsign_prefix", "Dump1090");
				int minTx = 15; int.TryParse(file.Read("min_tx_interval_secs", "Dump1090"), out minTx);

				_d1090 = new Dump1090Bridge(this, jurl, poll, sbsHost, sbsPort, sym, cmt, pref, minTx);
				_d1090.Start();
			}
			/* Dump1090Bridge */

			/* VesselFinderBridge */
			_vfEnabled = string.Equals(file.Read("enabled", "VesselFinder") ?? "false", "true", StringComparison.OrdinalIgnoreCase);
			if (_vfEnabled)
			{
				string jurl = file.Read("json_url", "VesselFinder");
				int poll = 10; int.TryParse(file.Read("poll_secs", "VesselFinder"), out poll);

				string sym  = file.Read("default_symbol", "VesselFinder");
				string cmt  = file.Read("comment_suffix", "VesselFinder");
				string pref = file.Read("node_callsign_prefix", "VesselFinder");
				int minTx = 30; int.TryParse(file.Read("min_tx_interval_secs", "VesselFinder"), out minTx);

				_vfBridge = new VesselFinderBridge(this, jurl, poll, sym, cmt, pref, minTx);
				_vfBridge.Start();
			}
			/* VesselFinderBridge */

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
			/* FlightAware */
			if (_faBridge != null) { try { _faBridge.Stop(); } catch { } _faBridge = null; }
			/* FlightAware */
			/* Dump1090Bridge */
			if (_d1090 != null) { try { _d1090.Stop(); } catch { } _d1090 = null; }
			/* Dump1090Bridge */
			/* VesselFinderBridge */
			if (_vfBridge != null) { try { _vfBridge.Stop(); } catch { } _vfBridge = null; }
			/* VesselFinderBridge */

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
					try { tcp_in_client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }
                    try
                    {
                        _state = "connecting";
                        tcp_in_client.Connect(server_in, port_in);
                        string txt2send = "user " + callsign + " pass " + passw + " vers APRSGateWay 1.0\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_in_client.GetStream().Write(arr, 0, arr.Length);
                        incomingMessagesCounter = 0;
                        _state = "Connected";
                        lastIncDT = DateTime.UtcNow;
						_reconnectDelayMs = 5000;
                    }
                    catch
                    {
						try { tcp_in_client.Close(); } catch {}
						tcp_in_client = null;
						Thread.Sleep(_reconnectDelayMs);
						if (_reconnectDelayMs < 60000) _reconnectDelayMs = Math.Min(60000, _reconnectDelayMs * 2);
							continue;
                    };
                };

                if (DateTime.UtcNow.Subtract(lastIncDT).TotalMinutes > 1)
                {
                    try
                    {
                        string txt2send = "#ping\r\n";
                        byte[] arr = System.Text.Encoding.GetEncoding(1251).GetBytes(txt2send);
                        tcp_in_client.GetStream().Write(arr, 0, arr.Length);
                        lastIncDT = DateTime.UtcNow;
                    }
                    catch
                    {
						if (tcp_in_client != null && IsConnected(tcp_in_client)) { continue; }
                    };
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
					if (tcp_in_client != null && IsConnected(tcp_in_client))
					{
						continue;
					}
					try { tcp_in_client.Close(); } catch {}
					tcp_in_client = null;
					Thread.Sleep(_reconnectDelayMs);
					if (_reconnectDelayMs < 60000) _reconnectDelayMs = Math.Min(60000, _reconnectDelayMs * 2);
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
				if (IsNullIslandAprs(line)) return;
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
