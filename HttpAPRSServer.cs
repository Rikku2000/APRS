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
using System.Globalization;

#if SQLITE
using System.Data;
using System.Data.SQLite;
#endif

namespace APRSWebServer
{
    public class HttpAPRSServer : SimpleServersPBAuth.ThreadedHttpServer
    {
        private APRSServer aprsServer = null;
        private Mutex wsMutex = new Mutex();
        private List<ClientRequest> wsClients = new List<ClientRequest>();

		public HttpAPRSServer(APRSServer aprsServer) : base(80) { 
			this.aprsServer = aprsServer; 
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
		}

		public HttpAPRSServer(APRSServer aprsServer, int Port) : base(Port) {
			this.aprsServer = aprsServer;
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
		}

		public HttpAPRSServer(APRSServer aprsServer, IPAddress IP, int Port) : base(IP, Port) {
			this.aprsServer = aprsServer;
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
		}

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
			SELECT p.recv_utc, p.callsign, p.lat, p.lon, p.course, p.speed, p.symbol, p.comment
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
							string cmt = rd.IsDBNull(7) ? "" : rd.GetString(7);

							cmt = cmt.Replace("\r", " ").Replace("\n", " ");
							string line = string.Format(
								CultureInfo.InvariantCulture,
								"{0} UTC {1} >> {2:000.0000000} {3:000.0000000} {4:00000.00} {5} {6} {7} {8}",
								when, call, lat, lon, 0.0, course, speed, symbol, cmt);

							rows.Add(line);
						}
					}
				}
			}
			return rows;
		}
#endif

		private static string HtmlEncode(string s)
		{
			if (string.IsNullOrEmpty(s)) return string.Empty;

			return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");
		}

		private static string SanitizeLogHtml(string s)
		{
			if (string.IsNullOrEmpty(s)) return string.Empty;

			s = Regex.Replace(s, "(?is)<(script|style)[^>]*>.*?</\\1>", string.Empty);
			s = Regex.Replace(s, @"\son\w+\s*=\s*(['""]).*?\1", string.Empty, RegexOptions.IgnoreCase);
			s = Regex.Replace(s, @"\shref\s*=\s*(['""])\s*javascript:[^'""]*\1", " href=\"#\"", RegexOptions.IgnoreCase);
			s = Regex.Replace(s, @"\ssrc\s*=\s*(['""])\s*javascript:[^'""]*\1", " src=\"#\"", RegexOptions.IgnoreCase);
			s = Regex.Replace(s, "(?is)</?(iframe|object|embed|img|svg|link|meta|base|form|input|button|textarea|select)[^>]*>", string.Empty);

			return s;
		}

		private void OutStatistics(ClientRequest clReq)
		{
			var sb = new StringBuilder(16 * 1024);

			ulong tcpAlive  = (ulong)this.ClientsAlive + (ulong)aprsServer.ClientsAlive;
			ulong aprsAlive = (ulong)aprsServer.ClientsAlive;

			int wsAlive;
			wsMutex.WaitOne();
			wsAlive = wsClients.Count;
			wsMutex.ReleaseMutex();

			ulong tcpTotal  = (ulong)this.ClientsCounter + (ulong)aprsServer.ClientsCounter;
			ulong aprsTotal = (ulong)aprsServer.ClientsCounter;
			ulong httpTotal = this.ClientsCounter;

			this.ResponseEncoding = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";

			sb.AppendLine("<!doctype html>");
			sb.AppendLine("<html lang=\"en\">");
			sb.AppendLine("<head>");
			sb.AppendLine("<meta charset=\"utf-8\"/>");
			sb.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"/>");
			sb.AppendLine("<title>APRS Server Statistics</title>");
			sb.AppendLine("<style>");
			sb.AppendLine(" :root { --bg:#0b0f13; --card:#11161c; --muted:#8aa0b4; --text:#e9f1f7; --accent:#3abef9; --ok:#20c997; --bad:#ff6b6b; }");
			sb.AppendLine(" body{margin:0;font:14px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Arial,sans-serif;background:var(--bg);color:var(--text);} ");
			sb.AppendLine(" .wrap{max-width:1100px;margin:0 auto;padding:20px;} ");
			sb.AppendLine(" header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:16px;} ");
			sb.AppendLine(" h1{font-size:20px;margin:0;} h1 span{color:var(--accent);} ");
			sb.AppendLine(" .muted{color:var(--muted);} ");
			sb.AppendLine(" .grid{display:flex;flex-wrap:wrap;gap:12px;} ");
			sb.AppendLine(" .card{background:var(--card);border-radius:12px;padding:14px;flex:1 1 240px;min-width:240px;box-shadow:0 1px 0 rgba(255,255,255,0.04) inset;} ");
			sb.AppendLine(" .k{font-size:12px;color:var(--muted);} .v{font-weight:600;font-size:18px;} ");
			sb.AppendLine(" details{margin-top:8px;} summary{cursor:pointer;outline:none;} ");
			sb.AppendLine(" ul{list-style:none;padding-left:0;margin:8px 0 0 0;} li{padding:6px 0;border-bottom:1px solid rgba(255,255,255,0.06);} ");
			sb.AppendLine(" .row{display:flex;justify-content:space-between;gap:10px;flex-wrap:wrap;} ");
			sb.AppendLine(" code,pre{font-family:ui-monospace,SFMono-Regular,Consolas,Menlo,monospace;} ");
			sb.AppendLine(" pre.log{max-height:320px;overflow:auto;background:#0d1218;border-radius:10px;padding:12px;border:1px solid rgba(255,255,255,0.06);} ");
			sb.AppendLine(" .tag{font-size:12px;padding:2px 6px;border-radius:999px;background:#0e141b;border:1px solid rgba(255,255,255,0.08);color:var(--muted);} ");
			sb.AppendLine(" .ok{color:var(--ok);} .bad{color:var(--bad);} ");
			sb.AppendLine(" .no-scrollbar{ -ms-overflow-style:none; scrollbar-width:none; } ");
			sb.AppendLine(" .no-scrollbar::-webkit-scrollbar{ display:none; } ");
			sb.AppendLine(" @media (max-width:640px){ .row{flex-direction:column;align-items:flex-start;} } ");
			sb.AppendLine("</style>");
			sb.AppendLine("</head>");
			sb.AppendLine("<body><div class=\"wrap\">");

			sb.AppendFormat("<header><h1>{0} <span>v{1}</span></h1>", HtmlEncode(ServerName), HtmlEncode(APRSServer.GetVersion()));
			sb.AppendFormat("<div class=\"muted\">Report time: {0:HH:mm:ss dd.MM.yyyy} UTC</div></header>", DateTime.UtcNow);

			sb.AppendLine("<div class=\"grid\">");

			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Current TCP connections</div><div class=\"v\">" 
				+ tcpAlive.ToString(CultureInfo.InvariantCulture) + "</div></div>");

			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Current APRS connections</div><div class=\"v\">" 
				+ aprsAlive.ToString(CultureInfo.InvariantCulture) + " <span class=\"tag\">port " 
				+ aprsServer.ServerPort.ToString(CultureInfo.InvariantCulture) + "</span></div></div>");

			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Current WebSocket connections</div><div class=\"v\">" 
				+ wsAlive.ToString(CultureInfo.InvariantCulture) + " <span class=\"tag\">port " 
				+ this.ServerPort.ToString(CultureInfo.InvariantCulture) + "</span></div></div>");

			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Totals</div><div class=\"row\">"
				+ "<div><div class=\"k\">TCP</div><div class=\"v\">" + tcpTotal.ToString(CultureInfo.InvariantCulture) + "</div></div>"
				+ "<div><div class=\"k\">APRS</div><div class=\"v\">" + aprsTotal.ToString(CultureInfo.InvariantCulture) + "</div></div>"
				+ "<div><div class=\"k\">HTTP</div><div class=\"v\">" + httpTotal.ToString(CultureInfo.InvariantCulture) + "</div></div>"
				+ "</div></div>");

			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Buddies in memory</div><div class=\"v\">" + (aprsServer.StoreGPSInMemory ? aprsServer.BUDs.Count.ToString() : "—") + "</div><div class=\"k\">" + (aprsServer.StoreGPSInMemory ? ("alive max " + aprsServer.StoreGPSMaxTime + " m") : "No Store GPS in Memory") + "</div></div>");
			sb.AppendLine("  <div class=\"card\"><div class=\"k\">Totals</div><div class=\"row\">");
			sb.AppendLine("      <div><div class=\"k\">TCP</div><div class=\"v\">" + tcpTotal + "</div></div>");
			sb.AppendLine("      <div><div class=\"k\">APRS</div><div class=\"v\">" + aprsTotal + "</div></div>");
			sb.AppendLine("      <div><div class=\"k\">HTTP</div><div class=\"v\">" + httpTotal + "</div></div>");
			sb.AppendLine("  </div></div>");
			sb.AppendLine("</div>");

			sb.AppendLine("<div class=\"grid\">");
			sb.AppendLine("<div class=\"card\">");
			sb.AppendLine("<details open><summary><strong>APRS clients</strong> (" + aprsAlive + ")</summary>");
			if (aprsServer.aprsClients.Count > 0)
			{
				aprsServer.aprsMutex.WaitOne();
				sb.AppendLine("<ul>");
				for (int i = 0; i < aprsServer.aprsClients.Count; i++)
				{
					var c = aprsServer.aprsClients[i];
					var ep = (System.Net.IPEndPoint)c.client.Client.RemoteEndPoint;
					string ip = ep.Address.ToString();
					int port = ep.Port;
					string user = HtmlEncode(c.user);
					string soft = HtmlEncode(c.SoftNam + " " + c.SoftVer);
					string ok = c.validated ? "<span class=\"ok\">pass ok</span>" : "<span class=\"bad\">pass ?</span>";
					sb.AppendFormat("<li><div class=\"row\"><div><code>{0}:{1}</code> as <strong>{2}</strong> {3}</div><div class=\"tag\">{4}</div></div></li>", HtmlEncode(ip), port, user, ok, soft);
				}
				sb.AppendLine("</ul>");
				aprsServer.aprsMutex.ReleaseMutex();
			}
			else
			{
				sb.AppendLine("<div class=\"muted\">No APRS clients</div>");
			}
			sb.AppendLine("</details>");
			sb.AppendLine("</div>");

			sb.AppendLine("<div class=\"card\">");
			sb.AppendLine("<details open><summary><strong>WebSocket clients</strong> (" + wsAlive + ")</summary>");
			if (wsAlive > 0)
			{
				wsMutex.WaitOne();
				sb.AppendLine("<ul>");
				for (int i = 0; i < wsClients.Count; i++)
				{
					var ep = (System.Net.IPEndPoint)wsClients[i].Client.Client.RemoteEndPoint;
					sb.AppendFormat("<li><code>{0}:{1}</code></li>", HtmlEncode(ep.Address.ToString()), ep.Port);
				}
				sb.AppendLine("</ul>");
				wsMutex.ReleaseMutex();
			}
			else
			{
				sb.AppendLine("<div class=\"muted\">No WebSocket clients</div>");
			}
			sb.AppendLine("</details>");
			sb.AppendLine("</div>");

			sb.AppendLine("<div class=\"card\">");
			sb.AppendLine("<details><summary><strong>Buddies in memory</strong></summary>");
			if (aprsServer.StoreGPSInMemory)
			{
				aprsServer.ClearBuds();
				if (aprsServer.BUDs.Count > 0)
				{
					aprsServer.budsMutex.WaitOne();
					sb.AppendLine("<ul>");
					for (int i = 0; i < aprsServer.BUDs.Count; i++)
					{
						var b = aprsServer.BUDs[i];
						double aliveMin = DateTime.UtcNow.Subtract(b.last).TotalMinutes;
						sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture,
							"<li><strong>{0}</strong>, last {1:HH:mm:ss dd.MM.yyyy} &raquo; {2} {3} - {4}; alive {5:0.} m</li>",
							HtmlEncode(b.name), b.last, b.lat, b.lon, HtmlEncode(b.iconSymbol), aliveMin);
					}
					sb.AppendLine("</ul>");
					aprsServer.budsMutex.ReleaseMutex();
				}
				else
				{
					sb.AppendLine("<div class=\"muted\">No buddies stored</div>");
				}
			}
			else
			{
				sb.AppendLine("<div class=\"muted\">No Store GPS in Memory</div>");
			}
			sb.AppendLine("</details>");
			sb.AppendLine("</div>");
			sb.AppendLine("</div>");

			sb.AppendLine("<div class=\"card\" style=\"margin-top:12px;\">");
			sb.AppendLine("<strong>Last APRS packets</strong>");
			string[] lap = aprsServer.LastAPRSPackets;
			if (lap != null && lap.Length > 0)
			{
				sb.AppendLine("<pre class=\"log no-scrollbar\">");
				foreach (var lp in lap)
				{
					sb.AppendLine(SanitizeLogHtml(lp));
				}
				sb.AppendLine("</pre>");
			}
			else
			{
				sb.AppendLine("<div class=\"muted\">No packets yet</div>");
			}
			sb.AppendLine("</div>");

			sb.AppendLine("</div></body></html>");

			HttpClientSendText(clReq.Client, sb.ToString());
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