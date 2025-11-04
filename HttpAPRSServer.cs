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
using System.Data;
using System.Data.SQLite;

using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace APRSWebServer
{
    public class HttpAPRSServer : SimpleServersPBAuth.ThreadedHttpServer
    {
        private APRSServer aprsServer = null;
        private Mutex wsMutex = new Mutex();
        private List<ClientRequest> wsClients = new List<ClientRequest>();

        private X509Certificate2 httpsCertificate = null;
        private bool httpsEnabled = false;
        private readonly Dictionary<TcpClient, SslStream> httpsStreams = new Dictionary<TcpClient, SslStream>();
        private readonly Mutex httpsMutex = new Mutex();

		public HttpAPRSServer(APRSServer aprsServer) : base(80) { 
			this.aprsServer = aprsServer; 
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
			InitHttps();
		}

		public HttpAPRSServer(APRSServer aprsServer, int Port) : base(Port) {
			this.aprsServer = aprsServer;
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
			InitHttps();
		}

		public HttpAPRSServer(APRSServer aprsServer, IPAddress IP, int Port) : base(IP, Port) {
			this.aprsServer = aprsServer;
			this.ResponseEncoding = Encoding.UTF8;
			this.RequestEncoding  = Encoding.UTF8;
			this.Headers["Content-type"] = "text/html; charset=utf-8";
			InitHttps();
		}

        ~HttpAPRSServer() { this.Dispose(); }

		public string HttpBuildPage (string title, string msg) {
			string server = HtmlEncode(ServerName);
			string ver = HtmlEncode(APRSServer.GetVersion());
			string ts = DateTime.UtcNow.ToString("HH:mm:ss dd.MM.yyyy", CultureInfo.InvariantCulture);

			string output = string.Format(CultureInfo.InvariantCulture,
		@"<!doctype html>
		<html lang=""en"">
		<head>
		<meta charset=""utf-8""/>
		<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
		<title>APRS-Server</title>
		<style>
		 :root {{ --bg:#0b0f13; --card:#11161c; --muted:#8aa0b4; --text:#e9f1f7; --accent:#3abef9; --ok:#20c997; --bad:#ff6b6b; }}
		 body{{margin:0;font:14px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Arial,sans-serif;background:var(--bg);color:var(--text);}}
		 .wrap{{max-width:1100px;margin:0 auto;padding:20px;}}
		 header{{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:16px;}}
		 h1{{font-size:20px;margin:0;}} h1 span{{color:var(--accent);}}
		 .muted{{color:var(--muted);}}
		 .card{{background:var(--card);border-radius:12px;padding:16px;box-shadow:0 1px 0 rgba(255,255,255,0.04) inset;}}
		 .card h2{{margin:0 0 8px;font-size:18px;}}
		 @media (max-width:640px){{ header{{flex-direction:column;align-items:flex-start;}} }}
		</style>
		</head>
		<body>
		<div class=""wrap"">
		  <header>
			<h1>{0} <span>v{1}</span></h1>
			<div class=""muted"">Report time: {2} UTC</div>
		  </header>
		  <div class=""card"">
			<h2>{3}</h2>
			<p class=""muted"">
			  {4}
			</p>
		  </div>
		</div>
		<center>- This Software was created by -</br><i><b>13MAD86 / Martin<b></i></center>
		</body>
		</html>", server, ver, ts, title, msg);

			return output;
		}

        private void InitHttps() {
            try {
                string baseDir = SimpleServersPBAuth.TTCPServer.GetCurrentDir();
                string pfxPath = Path.Combine(baseDir, APRSHTTPSFile);

                if (File.Exists(pfxPath)) {
                    httpsCertificate = new X509Certificate2(pfxPath, APRSHTTPSPassword);

                    httpsEnabled = true;
                    Console.WriteLine("\nHTTPS enabled using certificate: " + pfxPath + "\n");
                } else {
                    httpsEnabled = false;
                    Console.WriteLine("\nHTTPS: no .pfx file found, running HTTP only.\n");
                }
            } catch (Exception ex) {
                httpsEnabled = false;
                Console.WriteLine("\nHTTPS disabled: " + ex.GetType().Name + " - " + ex.Message + "\n");
            }
        }

        private void RegisterHttpsStream(TcpClient client, SslStream ssl) {
            httpsMutex.WaitOne();
            try {
                if (!httpsStreams.ContainsKey(client))
                    httpsStreams.Add(client, ssl);
                else
                    httpsStreams[client] = ssl;
            } finally {
                httpsMutex.ReleaseMutex();
            }
        }

        private SslStream GetHttpsStream(TcpClient client) {
            httpsMutex.WaitOne();
            try {
                SslStream ssl;
                if (httpsStreams.TryGetValue(client, out ssl))
                    return ssl;
                return null;
            } finally {
                httpsMutex.ReleaseMutex();
            }
        }

        private void UnregisterHttpsStream(TcpClient client) {
            httpsMutex.WaitOne();
            try {
                if (httpsStreams.ContainsKey(client))
                    httpsStreams.Remove(client);
            } finally {
                httpsMutex.ReleaseMutex();
            }
        }

        protected override void GetClient(TcpClient Client, ulong clientID) {
            if (!httpsEnabled || httpsCertificate == null) {
                base.GetClient(Client, clientID);
                return;
            }

            Regex CR = new Regex(@"Content-Length: (\d+)", RegexOptions.IgnoreCase);

            string Request = "";
            string Header = null;
            List<byte> Body = new List<byte>();

            int bRead = -1;
            int posCRLF = -1;
            int receivedBytes = 0;
            int contentLength = 0;

            SslStream ssl = null;

            try {
                ssl = new SslStream(Client.GetStream(), false);
                var protocols = SslProtocols.Tls12 | SslProtocols.Tls11 | SslProtocols.Tls;
                ssl.AuthenticateAsServer(httpsCertificate, false, protocols, false);
                RegisterHttpsStream(Client, ssl);

                while ((bRead = ssl.ReadByte()) >= 0) {
                    receivedBytes++;
                    Body.Add((byte)bRead);

                    if (_OnlyHTTP && (receivedBytes == 1)) {
                        if ((bRead != 0x47) && (bRead != 0x50)) {
                            onBadClient(Client, clientID, Body.ToArray());
                            return;
                        }
                    }

                    Request += (char)bRead;
                    if (bRead == 0x0A)
                        posCRLF = Request.IndexOf("\r\n\r\n");
                    if (posCRLF >= 0 || Request.Length > _MaxHeaderSize)
                        break;
                }

                if (Request.Length > _MaxHeaderSize) {
                    HttpClientSendError(Client, 414, "414 Header Too Long");
                    return;
                }

                bool valid = (posCRLF > 0);
                if ((!valid) && _OnlyHTTP) {
                    onBadClient(Client, clientID, Body.ToArray());
                    return;
                }

                if (valid) {
                    Body.Clear();
                    Header = Request;

                    Match mx = CR.Match(Request);
                    if (mx.Success)
                        contentLength = int.Parse(mx.Groups[1].Value);

                    if (contentLength > 0) {
                        Request = "";
                        while ((bRead = ssl.ReadByte()) >= 0) {
                            receivedBytes++;
                            Body.Add((byte)bRead);

                            string rcvd = _requestEnc.GetString(new byte[] { (byte)bRead }, 0, 1);
                            Request += rcvd;
                            if (Request.Length >= contentLength || Request.Length > _MaxBodySize)
                                break;
                        }

                        if (Request.Length > _MaxBodySize) {
                            HttpClientSendError(Client, 413, "413 Request Entity Too Large");
                            return;
                        }
                    }
                }

                base.GetClientRequest(Client, clientID, Request, Header, Body.ToArray());
            } catch (Exception ex) {
                LastError = ex;
                LastErrTime = DateTime.Now;
                ErrorsCounter++;
                onError(Client, clientID, ex);
            }
        }

        protected override void HttpClientSendData(TcpClient Client, byte[] body, IDictionary<string, string> dopHeaders, int ResponseCode, string ContentType) {
            if (!httpsEnabled) {
                base.HttpClientSendData(Client, body, dopHeaders, ResponseCode, ContentType);
                return;
            }

            SslStream ssl = GetHttpsStream(Client);
            if (ssl == null) {
                base.HttpClientSendData(Client, body, dopHeaders, ResponseCode, ContentType);
                return;
            }

            string header = "HTTP/1.1 " + ResponseCode.ToString() + "\r\n";

            string val = null;
            if (dopHeaders != null && (val = DictGetKeyIgnoreCase(dopHeaders, "Status")) != null)
                header = "HTTP/1.1 " + val + "\r\n";

            _headers_mutex.WaitOne();
            try {
                foreach (KeyValuePair<string, string> kvp in _headers)
                    header += string.Format("{0}: {1}\r\n", kvp.Key, kvp.Value);
            } finally { _headers_mutex.ReleaseMutex(); }

            if (dopHeaders != null) {
                foreach (KeyValuePair<string, string> kvp in dopHeaders) {
                    if (string.Equals(kvp.Key, "Status", StringComparison.OrdinalIgnoreCase))
                        continue;
                    header += string.Format("{0}: {1}\r\n", kvp.Key, kvp.Value);
                }
            }

            if (!DictHasKeyIgnoreCase(dopHeaders, "Content-type"))
                header += "Content-type: " + ContentType + "\r\n";
            if (!DictHasKeyIgnoreCase(dopHeaders, "Content-Length"))
                header += "Content-Length: " + (body != null ? body.Length.ToString() : "0") + "\r\n";
            header += "\r\n";

            byte[] headerBytes = Encoding.GetEncoding(1251).GetBytes(header);

            ssl.Write(headerBytes, 0, headerBytes.Length);
            if (body != null && body.Length > 0)
                ssl.Write(body, 0, body.Length);
            ssl.Flush();
        }

        protected override void HttpClientSendData(TcpClient Client, byte[] body) {
            HttpClientSendData(Client, body, null, 200, "text/html");
        }

        protected override void HttpClientSendData(TcpClient Client, byte[] body, IDictionary<string, string> dopHeaders) {
            HttpClientSendData(Client, body, dopHeaders, 200, "text/html");
        }

        protected override void HttpClientSendData(TcpClient Client, byte[] body, IDictionary<string, string> dopHeaders, int ResponseCode) {
            HttpClientSendData(Client, body, dopHeaders, ResponseCode, "text/html");
        }

        protected override void HttpClientSendData(TcpClient Client, byte[] body, IDictionary<string, string> dopHeaders, string ContentType) {
            HttpClientSendData(Client, body, dopHeaders, 200, ContentType);
        }

        protected override void HttpClientSendText(TcpClient Client, string Text, IDictionary<string, string> dopHeaders) {
            HttpClientSendData(Client, ResponseEncoding.GetBytes(Text), dopHeaders, 200, "text/html");
        }

        protected override void HttpClientSendText(TcpClient Client, string Text) {
            HttpClientSendText(Client, Text, null);
        }

		protected override void HttpClientSendError( TcpClient Client, int Code, Dictionary<string, string> dopHeaders) {
			string html;

			if (Code == 404) {
				string server = HtmlEncode(ServerName);
				string ver    = HtmlEncode(APRSServer.GetVersion());
				string ts     = DateTime.UtcNow.ToString("HH:mm:ss dd.MM.yyyy", CultureInfo.InvariantCulture);

				html = string.Format(CultureInfo.InvariantCulture,
		@"<!doctype html>
		<html lang=""en"">
		<head>
		<meta charset=""utf-8""/>
		<meta name=""viewport"" content=""width=device-width, initial-scale=1""/>
		<title>APRS-Server - 404</title>
		<style>
		 :root {{ --bg:#0b0f13; --card:#11161c; --muted:#8aa0b4; --text:#e9f1f7; --accent:#3abef9; --ok:#20c997; --bad:#ff6b6b; }}
		 body{{margin:0;font:14px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Arial,sans-serif;background:var(--bg);color:var(--text);}}
		 .wrap{{max-width:1100px;margin:0 auto;padding:20px;}}
		 header{{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:16px;}}
		 h1{{font-size:20px;margin:0;}} h1 span{{color:var(--accent);}}
		 .muted{{color:var(--muted);}}
		 .card{{background:var(--card);border-radius:12px;padding:16px;box-shadow:0 1px 0 rgba(255,255,255,0.04) inset;}}
		 .card h2{{margin:0 0 8px;font-size:18px;}}
		 @media (max-width:640px){{ header{{flex-direction:column;align-items:flex-start;}} }}
		</style>
		</head>
		<body>
		<div class=""wrap"">
		  <header>
			<h1>{0} <span>v{1}</span></h1>
			<div class=""muted"">Report time: {2} UTC</div>
		  </header>
		  <div class=""card"">
			<h2>404 – Page not found</h2>
			<p class=""muted"">
			  The requested page could not be found on this APRS server.
			  It might have been moved, removed or the URL is wrong.
			</p>
		  </div>
		</div>
		<center>- This Software was created by -</br><i><b>13MAD86 / Martin<b></i></center>
		</body>
		</html>", server, ver, ts);
			} else {
				string codeStr = Code.ToString() + " " + ((HttpStatusCode)Code).ToString();
				html = "<!DOCTYPE html><html><body><h1>" + codeStr + "</h1></body></html>";
			}

			byte[] body = ResponseEncoding.GetBytes(html);
			HttpClientSendData(Client, body, dopHeaders, Code, "text/html; charset=utf-8");
		}

        protected override void HttpClientSendError(TcpClient Client, int Code) {
            HttpClientSendError(Client, Code, (Dictionary<string, string>)null);
        }

        protected override void HttpClientSendError(TcpClient Client, int Code, string Text) {
            string html = "<!DOCTYPE html><html><body><h1>" +
                          Code.ToString() + " " + ((HttpStatusCode)Code).ToString() +
                          "</h1><p>" + System.Web.HttpUtility.HtmlEncode(Text ?? "") +
                          "</p></body></html>";

            byte[] body = ResponseEncoding.GetBytes(html);
            HttpClientSendData(Client, body, null, Code, "text/html; charset=utf-8");
        }


        protected override void HttpClientSendFile(TcpClient Client, string fileName, Dictionary<string, string> dopHeaders, int ResponseCode, string ContentType) {
            if (!File.Exists(fileName)) {
                HttpClientSendError(Client, 404);
                return;
            }

            FileInfo fi = new FileInfo(fileName);
            byte[] body = File.ReadAllBytes(fileName);

            if (string.IsNullOrEmpty(ContentType))
                ContentType = GetMemeType(fi.Extension.ToLower());

            HttpClientSendData(Client, body, dopHeaders, ResponseCode, ContentType);
        }

        protected override void GetClientRequest(ClientRequest Request)
        {
            if (Request.Query.StartsWith("/statistics")) {
                OutStatistics(Request);
                return;
            };

			if (Request.Query.StartsWith("/admin")) {
				OutAdmin(Request);
				return;
			}

			if (Request.Query == "/userdata")
			{
				try
				{
					var lines = new List<string>();

					const string sql = @"
					WITH latest AS (
						SELECT callsign, MAX(recv_utc) AS recv_utc
						FROM positions
						GROUP BY callsign
					)
					SELECT p.recv_utc, p.callsign, p.lat, p.lon, p.course, p.speed, p.symbol, p.comment
					FROM positions p
					JOIN latest l ON l.callsign = p.callsign AND l.recv_utc = p.recv_utc
					ORDER BY p.recv_utc DESC;";

					using (var conn = new SQLiteConnection(string.Format("Data Source={0};Read Only=True;", "aprs.sqlite")))
					{
						conn.Open();
						using (var cmd = new SQLiteCommand(sql, conn))
						using (var rd = cmd.ExecuteReader(CommandBehavior.CloseConnection))
						{
							while (rd.Read())
							{
								var dt = DateTime.SpecifyKind(DateTime.Parse(rd.GetString(0), CultureInfo.InvariantCulture), DateTimeKind.Utc);
								string when = dt.ToString("HH:mm:ss dd.MM.yyyy", CultureInfo.InvariantCulture);

								string call   = rd.IsDBNull(1) ? "" : rd.GetString(1).ToUpperInvariant();
								double lat    = rd.GetDouble(2);
								double lon    = rd.GetDouble(3);
								int course    = rd.IsDBNull(4) ? 0 : Convert.ToInt32(rd.GetValue(4), CultureInfo.InvariantCulture);
								int speed     = rd.IsDBNull(5) ? 0 : Convert.ToInt32(rd.GetValue(5), CultureInfo.InvariantCulture);
								string symbol = rd.IsDBNull(6) ? "//" : rd.GetString(6);
								string cmt    = rd.IsDBNull(7) ? "" : rd.GetString(7).Replace("\r"," ").Replace("\n"," ");

								string line = string.Format(
									CultureInfo.InvariantCulture,
									"{0} UTC {1} >> {2:000.0000000} {3:000.0000000} {4:00000.00} {5} {6} {7} {8}",
									when, call, lat, lon, 0.0, course, speed, symbol, cmt);

								lines.Add(line);
							}
						}
					}

					this.Headers["Content-type"] = "text/plain; charset=utf-8";
					HttpClientSendText(Request.Client, string.Join("\n", lines.ToArray()));
					return;
				}
				catch (Exception ex)
				{
					this.Headers["Content-type"] = "text/plain; charset=utf-8";
					HttpClientSendText(Request.Client, HttpBuildPage("Snapshot error", ex.Message));
					return;
				}
			}

            if (!HttpClientWebSocketInit(Request, false))
                PassFileToClientByRequest(Request, GetCurrentDir() + @"\map");
        }

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
			sb.AppendLine("<title>APRS-Server - Statistics</title>");
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
			sb.AppendLine("</div></div></div>");

			sb.AppendLine("<center>- This Software was created by -</br><i><b>13MAD86 / Martin<b></i></center>");
			sb.AppendLine("</body></html>");

			HttpClientSendText(clReq.Client, sb.ToString());
		}

		private string AdminField(string name, string value)
		{
			return "<div><label for='" + HtmlEncode(name) + "'>" + HtmlEncode(name)
				 + "</label><input id='" + HtmlEncode(name) + "' name='" + HtmlEncode(name)
				 + "' value='" + HtmlEncode(value) + "'/></div>";
		}

		private string AdminCheckbox(string name, string value)
		{
			bool isChecked = false;
			if (!string.IsNullOrEmpty(value))
				isChecked = value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase);

			return "<div>"
				 + "<label for='" + HtmlEncode(name) + "'>" + HtmlEncode(name) + "</label>"
				 + "<input type='checkbox' id='" + HtmlEncode(name) + "' name='" + HtmlEncode(name) + "' value='1' "
				 + (isChecked ? "checked " : "")
				 + "/>"
				 + "</div>";
		}

		private static string GetCfg(XmlDocument xd, string name)
		{
			var n = xd.SelectSingleNode("/config/" + name);
			return n != null ? n.InnerText : "";
		}

		private static void SetCfg(XmlDocument xd, string name, string val)
		{
			var n = xd.SelectSingleNode("/config/" + name);
			if (n == null)
			{
				var cfg = xd.SelectSingleNode("/config");
				if (cfg == null)
				{
					cfg = xd.CreateElement("config");
					xd.AppendChild(cfg);
				}
				n = xd.CreateElement(name);
				cfg.AppendChild(n);
			}
			n.InnerText = val ?? "";
		}

		private void OutAdmin(ClientRequest req)
		{
			string rip = req.RemoteIP ?? "";
			if (rip != "127.0.0.1" && rip != "::1")
			{
				this.Headers["Content-type"] = "text/html; charset=utf-8";
				HttpClientSendText(req.Client, HttpBuildPage("403 Forbidden", "Admin is local-only. Connect from 127.0.0.1."));
				return;
			}

			string xmlPath = Path.Combine(GetCurrentDir(), "aprs.xml");
			var sb = new StringBuilder(64 * 1024);
			this.Headers["Content-type"] = "text/html; charset=utf-8";

			string qs = "";
			int qi = req.Query.IndexOf('?');
			if (qi >= 0 && qi + 1 < req.Query.Length) qs = req.Query.Substring(qi + 1);
			var nv = System.Web.HttpUtility.ParseQueryString(qs);

			bool saving = string.Equals(nv["save"], "1", StringComparison.Ordinal);
			string msg = null;

			var xd = new XmlDocument();
			xd.PreserveWhitespace = true;
			try { xd.Load(xmlPath); }
			catch (Exception ex)
			{
				HttpClientSendText(req.Client, HttpBuildPage("Config error", HtmlEncode(ex.Message)));
				return;
			}

			if (saving)
			{
				string[] keys =
				{
					"ServerName","ServerPort","MaxClients","HTTPServer","APRSHTTPSFile",
					"APRSHTTPSPassword","APRSDatabaseFile","StoreGPSMaxTime","ListenIPMode"
				};
				foreach (var k in keys)
				{
					if (nv[k] != null) SetCfg(xd, k, nv[k]);
				}

				string[] checkboxKeys = new[]{
					"EnableClientFilter","PassBackAPRSPackets","OnlyValidPasswordUsers","PassDataOnlyValidUsers",
					"PassDataOnlyLoggedUser","StoreGPSInMemory","ListenMacMode","OutConfigToConsole",
					"OutAPRStoConsole","OutConnectionsToConsole","OutBroadcastsMessages","OutBuddiesCount"
				};
				foreach (var k in checkboxKeys)
				{
					var raw = nv[k];
					string val = (!string.IsNullOrEmpty(raw) && (raw == "1" || raw.Equals("true", StringComparison.OrdinalIgnoreCase))) ? "1" : "0";
					SetCfg(xd, k, val);
				}

				try { xd.Save(xmlPath); msg = "Saved."; }
				catch (Exception ex) { msg = "Save failed: " + HtmlEncode(ex.Message); }
			}

			sb.AppendLine("<!doctype html><html><head><meta charset='utf-8'/>");
			sb.AppendLine("<meta name='viewport' content='width=device-width, initial-scale=1'/>");
			sb.AppendLine("<title>APRS-Server - Admin</title>");
			sb.AppendLine("<style>");
			sb.AppendLine(" :root { --bg:#0b0f13; --card:#11161c; --muted:#8aa0b4; --text:#e9f1f7; --accent:#3abef9; --ok:#20c997; --bad:#ff6b6b; }");
			sb.AppendLine(" body{margin:0;font:14px/1.45 -apple-system,BlinkMacSystemFont,Segoe UI,Roboto,Arial,sans-serif;background:var(--bg);color:var(--text);} ");
			sb.AppendLine(" .wrap{max-width:1100px;margin:0 auto;padding:20px;} ");
			sb.AppendLine(" header{display:flex;align-items:baseline;justify-content:space-between;gap:12px;flex-wrap:wrap;margin-bottom:16px;} ");
			sb.AppendLine(" h1{font-size:20px;margin:0;} h1 span{color:var(--accent);} ");
			sb.AppendLine(" .muted{color:var(--muted);} ");
			sb.AppendLine(".card{background:#11161c;border-radius:12px;padding:16px;margin-top:12px}");
			sb.AppendLine("label{display:block;font-size:12px;color:#8aa0b4;margin-top:10px}");
			sb.AppendLine("input,select{width:100%;padding:8px;border-radius:8px;border:1px solid #22303c;background:#0e141b;color:#e9f1f7}");
			sb.AppendLine(".row{display:grid;grid-template-columns:1fr 1fr;gap:12px}");
			sb.AppendLine("@media (max-width:700px){.row{grid-template-columns:1fr}}");
			sb.AppendLine(".btns{display:flex;gap:8px;margin-top:16px}");
			sb.AppendLine(".btn{padding:10px 14px;border-radius:10px;border:1px solid #22303c;background:#0e141b;color:#e9f1f7;text-decoration:none;display:inline-block}");
			sb.AppendLine(".btn.primary{background:#3abef9;border-color:#3abef9;color:#091017;font-weight:600}");
			sb.AppendLine(".msg{margin-top:8px;color:#20c997}");
			sb.AppendLine("</style></head><body><div class='wrap'>");

			sb.AppendFormat("<header><h1>{0} <span>v{1}</span></h1>", HtmlEncode(ServerName), HtmlEncode(APRSServer.GetVersion()));
			sb.AppendFormat("<div class=\"muted\">Report time: {0:HH:mm:ss dd.MM.yyyy} UTC</div></header>", DateTime.UtcNow);

			if (!string.IsNullOrEmpty(msg)) sb.AppendLine("<div class='msg'>" + msg + "</div>");

			sb.AppendLine("<div class='card'><form method='GET' action='/admin'>");
			sb.AppendLine("<input type='hidden' name='save' value='1'/>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminField("Server Name", GetCfg(xd, "ServerName")));
			sb.AppendLine(AdminField("Server Port", GetCfg(xd, "ServerPort")));
			sb.AppendLine(AdminField("Max Clients", GetCfg(xd, "MaxClients")));
			sb.AppendLine(AdminField("HTTP Server", GetCfg(xd, "HTTPServer")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminField("HTTPS File", GetCfg(xd, "APRSHTTPSFile")));
			sb.AppendLine(AdminField("HTTPS Password", GetCfg(xd, "APRSHTTPSPassword")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminField("Database File", GetCfg(xd, "APRSDatabaseFile")));
			sb.AppendLine(AdminCheckbox("Store GPS In Memory", GetCfg(xd, "StoreGPSInMemory")));
			sb.AppendLine(AdminField("Store GPS Max Time", GetCfg(xd, "StoreGPSMaxTime")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminCheckbox("Enable Client Filter", GetCfg(xd, "EnableClientFilter")));
			sb.AppendLine(AdminCheckbox("Pass Back APRS Packets", GetCfg(xd, "PassBackAPRSPackets")));
			sb.AppendLine(AdminCheckbox("Only Valid Password Users", GetCfg(xd, "OnlyValidPasswordUsers")));
			sb.AppendLine(AdminCheckbox("Pass Data Only Valid Users", GetCfg(xd, "PassDataOnlyValidUsers")));
			sb.AppendLine(AdminCheckbox("Pass Data Only Logged User", GetCfg(xd, "PassDataOnlyLoggedUser")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminField("Listen IP Mode", GetCfg(xd, "ListenIPMode")));
			sb.AppendLine(AdminCheckbox("Listen Mac Mode", GetCfg(xd, "ListenMacMode")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='row'>");
			sb.AppendLine(AdminCheckbox("OutConfig To Console", GetCfg(xd, "OutConfigToConsole")));
			sb.AppendLine(AdminCheckbox("OutAPRS to Console", GetCfg(xd, "OutAPRStoConsole")));
			sb.AppendLine(AdminCheckbox("Out Connections To Console", GetCfg(xd, "OutConnectionsToConsole")));
			sb.AppendLine(AdminCheckbox("Out Broadcasts Messages", GetCfg(xd, "OutBroadcastsMessages")));
			sb.AppendLine(AdminCheckbox("Out Buddies Count", GetCfg(xd, "OutBuddiesCount")));
			sb.AppendLine("</div>");

			sb.AppendLine("<div class='btns'>");
			sb.AppendLine("<button class='btn primary' type='submit'>Save</button>");
			sb.AppendLine("</div>");

			sb.AppendLine("</form></div></div>");

			sb.AppendLine("<center>- This Software was created by -</br><i><b>13MAD86 / Martin<b></i></center>");
			sb.AppendLine("</body></html>");

			HttpClientSendText(req.Client, sb.ToString());
		}

        protected override void OnWebSocketClientConnected(ClientRequest clientRequest)
        {
            wsMutex.WaitOne();
            wsClients.Add(clientRequest);
            wsMutex.ReleaseMutex();

            byte[] ba = GetWebSocketFrameFromString("Welcome to " + ServerName);
            clientRequest.Client.GetStream().Write(ba, 0, ba.Length);

			try {
				var sock = clientRequest.Client.Client;
				sock.NoDelay = true;
				sock.SendBufferSize = 1 << 20;
				sock.ReceiveBufferSize = 1 << 20;
				sock.LingerState = new LingerOption(false, 0);
			} catch { }

            if (aprsServer.OutConnectionsToConsole)
                Console.WriteLine("WebSocket connected from: {0}:{1}, total {2}", clientRequest.RemoteIP, ((IPEndPoint)clientRequest.Client.Client.RemoteEndPoint).Port, wsClients.Count);

			if (aprsServer.StoreGPSInMemory) {
				PassBuds(clientRequest);
			} else {
				foreach (var line in GetLatestPositionsFromDb(APRSDatabaseFile, 2000)) {
					byte[] payload = GetWebSocketFrameFromString(line + "\r\n");

					wsMutex.WaitOne();
					try {
						for (int i = 0; i < wsClients.Count; i++) {
							var s = wsClients[i];
							try {
								var ns = s.Client.GetStream();
								ns.Write(payload, 0, payload.Length);
							} catch { }
						}
					} finally { wsMutex.ReleaseMutex(); }
				}
			}
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
            if (wsClients.Count > 0) {
                if (aprsServer.OutBroadcastsMessages)
                    Console.WriteLine("Broadcast WS: {0}", text.Replace("\r", "").Replace("\n", ""));

                for (int i = 0; i < wsClients.Count; i++) {
                    try {
                        wsClients[i].Client.GetStream().Write(toSend, 0, toSend.Length);
                    } catch { };
                };
            };
            wsMutex.ReleaseMutex();
        }
    }
}