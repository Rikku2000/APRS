using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Text.RegularExpressions;
using System.IO;
using System.Web;
using System.Xml;
using System.Xml.Serialization;
using System.Security;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Reflection;

#if SQLITE
using System.Data;
using System.Data.SQLite;
using System.Globalization;
#endif

namespace APRSWebServer
{
    public class HttpAPRSServer : SimpleServersPBAuth.ThreadedHttpServer
    {
        private APRSServer aprsServer = null;
        private Mutex wsMutex = new Mutex();
        private List<ClientRequest> wsClients = new List<ClientRequest>();

        public HttpAPRSServer(APRSServer aprsServer) : base(80) { this.aprsServer = aprsServer; }
        public HttpAPRSServer(APRSServer aprsServer, int Port) : base(Port) { this.aprsServer = aprsServer; }
        public HttpAPRSServer(APRSServer aprsServer, IPAddress IP, int Port) : base(IP, Port) { this.aprsServer = aprsServer; }
        ~HttpAPRSServer() { this.Dispose(); }

        protected override void GetClientRequest(ClientRequest Request)
        {
            if (Request.Query.StartsWith("/statistics"))
            {
                OutStatistics(Request);
                return;
            };

            if (!HttpClientWebSocketInit(Request, false))
                PassFileToClientByRequest(Request, GetCurrentDir() + @"\map");
        }

#if SQLITE
		private IEnumerable<string> GetLatestPositionsFromDb(string dbPath, int limit)
		{
			var rows = new List<string>();
			if (!File.Exists(dbPath)) return rows;

			const string sql = @"
			WITH latest AS (
				SELECT callsign, MAX(recv_utc) AS recv_utc
				FROM positions
				GROUP BY callsign
			)
			SELECT p.recv_utc, p.callsign, p.lat, p.lon, p.course, p.speed, p.symbol
			FROM positions p
			JOIN latest l ON l.callsign = p.callsign AND l.recv_utc = p.recv_utc
			ORDER BY p.recv_utc DESC
			LIMIT @lim;";

			using (var conn = new SQLiteConnection(string.Format("Data Source={0};Read Only=True;", dbPath)))
			{
				conn.Open();
				using (var cmd = new SQLiteCommand(sql, conn))
				{
					cmd.Parameters.AddWithValue("@lim", limit);
					using (var rd = cmd.ExecuteReader(CommandBehavior.CloseConnection))
					{
						while (rd.Read())
						{
							DateTime dt = DateTime.SpecifyKind(DateTime.Parse(rd.GetString(0), CultureInfo.InvariantCulture), DateTimeKind.Utc);
							string when = dt.ToString("HH:mm:ss dd.MM.yyyy", CultureInfo.InvariantCulture);

							string call   = rd.IsDBNull(1) ? "" : rd.GetString(1).ToUpperInvariant();
							double lat    = rd.GetDouble(2);
							double lon    = rd.GetDouble(3);
							int course    = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture);
							int speed     = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5), CultureInfo.InvariantCulture);
							string symbol = rd.IsDBNull(6) ? "//" : rd.GetString(6);

							string line = string.Format(
								CultureInfo.InvariantCulture,
								"{0} UTC {1} >> {2:000.0000000} {3:000.0000000} {4:00000.00} {5} {6} {7}",
								when, call, lat, lon, 0.0, course, speed, symbol);

							rows.Add(line);
						}
					}
				}
			}
			return rows;
		}
#endif

        private void OutStatistics(ClientRequest clReq)
        {
            string text = "<h2>" + ServerName + " v" + APRSServer.GetVersion() + "</h2>";
            text += "<b>Statistics</b>:<br/>\r\n<br/>\r\n";
            text += String.Format(" &nbsp; &nbsp; Current <b>TCP</b> connections: {0}<br/>\r\n", new object[] { this.ClientsAlive + aprsServer.ClientsAlive });
            text += String.Format(" &nbsp; &nbsp; Current <b>APRS</b> connections: {0} <span style=\"color:silver;\">at port {1}</span><br/>\r\n", new object[] { aprsServer.ClientsAlive, aprsServer.ServerPort });            
            if (aprsServer.aprsClients.Count > 0)
            {
                aprsServer.aprsMutex.WaitOne();
                for (int i = 0; i < aprsServer.aprsClients.Count; i++)
                    text += String.Format(" &nbsp; &nbsp; &nbsp; &nbsp;  - {0}:{1} as {2} {3} via {4}<br/>\r\n", new object[] { ((IPEndPoint)aprsServer.aprsClients[i].client.Client.RemoteEndPoint).Address.ToString(), ((IPEndPoint)aprsServer.aprsClients[i].client.Client.RemoteEndPoint).Port, aprsServer.aprsClients[i].user, aprsServer.aprsClients[i].validated ? " - passw ok" : "", aprsServer.aprsClients[i].SoftNam + " " + aprsServer.aprsClients[i].SoftVer });
                aprsServer.aprsMutex.ReleaseMutex();
            };
            text += String.Format(" &nbsp; &nbsp; Current <b>WebSocket</b> connections: {0} <span style=\"color:silver;\">at port {1}</span><br/>\r\n", new object[] { wsClients.Count, this.ServerPort });
            if (wsClients.Count > 0)
            {
                wsMutex.WaitOne();
                for (int i = 0; i < wsClients.Count; i++)
                    text += String.Format(" &nbsp; &nbsp; &nbsp; &nbsp;  - {0}:{1}<br/>\r\n", new object[] { ((IPEndPoint)wsClients[i].Client.Client.RemoteEndPoint).Address.ToString(), ((IPEndPoint)wsClients[i].Client.Client.RemoteEndPoint).Port });
                wsMutex.ReleaseMutex();
            };
            if (aprsServer.StoreGPSInMemory)
            {
                aprsServer.ClearBuds();
                text += String.Format(" &nbsp; &nbsp; Current <b>Buddies</b> in Memory: {0}, max alive {1} m<br/>\r\n", new object[] { aprsServer.BUDs.Count, aprsServer.StoreGPSMaxTime });
                if (aprsServer.BUDs.Count > 0)
                {
                    aprsServer.budsMutex.WaitOne();
                    for (int i = 0; i < aprsServer.BUDs.Count; i++)
                        text += String.Format(System.Globalization.CultureInfo.InvariantCulture,
                            " &nbsp; &nbsp; &nbsp; &nbsp;  - {0}, last {1:HH:mm:ss dd.MM.yyyy} >> {2} {3} - {4}; alive {5:0.} m<br/>\r\n", new object[] { aprsServer.BUDs[i].name, aprsServer.BUDs[i].last, aprsServer.BUDs[i].lat, aprsServer.BUDs[i].lon, aprsServer.BUDs[i].iconSymbol, DateTime.UtcNow.Subtract(aprsServer.BUDs[i].last).TotalMinutes });
                    aprsServer.budsMutex.ReleaseMutex();
                };
            }
            else
                text += " &nbsp; &nbsp; No Store GPS in Memory<br/>\r\n";
            text += "<br/>\r\n";
            text += String.Format(" &nbsp; &nbsp; Total <b>TCP</b> Clients Counter: {0}<br/>\r\n", this.ClientsCounter + aprsServer.ClientsCounter);
            text += String.Format(" &nbsp; &nbsp; Total <b>APRS</b> Clients Counter: {0}<br/>\r\n", aprsServer.ClientsCounter);
            text += String.Format(" &nbsp; &nbsp; Total <b>HTTP</b> Clients Counter: {0}<br/>\r\n", this.ClientsCounter);            
            text += "<br/><hr/>";
            text += "Last APRS Packets:<br/>";
            string[] lap = aprsServer.LastAPRSPackets;
            if ((lap != null) && (lap.Length > 0))
                foreach (string lp in lap)
                    text += " &nbsp; " + lp + "<br/>";
            text += "<hr/>";
            text += String.Format("Report time: {0:HH:mm:ss dd.MM.yyyy} UTC", DateTime.UtcNow);
            HttpClientSendText(clReq.Client, text);
        }

        protected override void OnWebSocketClientConnected(ClientRequest clientRequest)
        {
            wsMutex.WaitOne();
            wsClients.Add(clientRequest);
            wsMutex.ReleaseMutex();

            byte[] ba = GetWebSocketFrameFromString("Welcome to " + ServerName);
            clientRequest.Client.GetStream().Write(ba, 0, ba.Length);
            clientRequest.Client.GetStream().Flush();

            if (aprsServer.OutConnectionsToConsole)
                Console.WriteLine("WebSocket connected from: {0}:{1}, total {2}", clientRequest.RemoteIP, ((IPEndPoint)clientRequest.Client.Client.RemoteEndPoint).Port, wsClients.Count);

#if SQLITE
			if (aprsServer.StoreGPSInMemory) {
				PassBuds(clientRequest);
			} else {
				foreach (var line in GetLatestPositionsFromDb(APRSDatabaseFile, 2000)) {
					var payload = GetWebSocketFrameFromString(line + "\r\n");
					try {
						clientRequest.Client.GetStream().Write(payload, 0, payload.Length);
						clientRequest.Client.GetStream().Flush();
					} catch { }
				}
			}
#else
            PassBuds(clientRequest);

            if ((aprsServer.OutBroadcastsMessages) && (aprsServer.BUDs.Count > 0))
                Console.WriteLine("Passed {0} buddies to WS {1}:{2}", aprsServer.BUDs.Count, clientRequest.RemoteIP, ((IPEndPoint)clientRequest.Client.Client.RemoteEndPoint).Port);
#endif

        }

        protected override void OnWebSocketClientDisconnected(ClientRequest clientRequest)
        {
            wsMutex.WaitOne();
            for (int i = 0; i < wsClients.Count; i++)
                if (wsClients[i].clientID == clientRequest.clientID)
                {
                    wsClients.RemoveAt(i);
                    break;
                };
            wsMutex.ReleaseMutex();

            if (aprsServer.OutConnectionsToConsole)
                Console.WriteLine("WebSocket disconnected from: {0}:{1}, total {2}", clientRequest.RemoteIP, ((IPEndPoint)clientRequest.Client.Client.RemoteEndPoint).Port, wsClients.Count);
        }

        protected override void OnWebSocketClientData(ClientRequest clientRequest, byte[] data)
        {
            try
            {
                string fws = GetStringFromWebSocketFrame(data, data.Length);
                if (String.IsNullOrEmpty(fws)) return;

                string tws = fws + " ok";
                byte[] toSend = GetWebSocketFrameFromString(tws);
                clientRequest.Client.GetStream().Write(toSend, 0, toSend.Length);
                clientRequest.Client.GetStream().Flush();
            }
            catch { };
        }

        private void PassBuds(ClientRequest cr)
        {
            if (!aprsServer.StoreGPSInMemory) return;
            aprsServer.ClearBuds();

            aprsServer.budsMutex.WaitOne();
            if (aprsServer.BUDs.Count > 0)
                for (int i = 0; i < aprsServer.BUDs.Count; i++)
                {
                    string text = aprsServer.BUDs[i].GetWebSocketText() + "\r\n";
                    byte[] toSend = GetWebSocketFrameFromString(text);
                    try
                    {
                        cr.Client.GetStream().Write(toSend, 0, toSend.Length);
                        cr.Client.GetStream().Flush();
                    }
                    catch { };
                };
            aprsServer.budsMutex.ReleaseMutex();
        }

        public void Broadcast(APRSData.Buddie bud)
        {
            string text = bud.GetWebSocketText() + "\r\n";
            byte[] toSend = GetWebSocketFrameFromString(text);
            wsMutex.WaitOne();
            if (wsClients.Count > 0)
            {
                if (aprsServer.OutBroadcastsMessages)
                    Console.WriteLine("Broadcast WS: {0}", text.Replace("\r", "").Replace("\n", ""));
                for (int i = 0; i < wsClients.Count; i++)
                {
                    try
                    {
                        wsClients[i].Client.GetStream().Write(toSend, 0, toSend.Length);
                        wsClients[i].Client.GetStream().Flush();
                    }
                    catch { };
                };
            };
            wsMutex.ReleaseMutex();
        }
    }
}