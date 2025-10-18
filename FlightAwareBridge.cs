using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace APRSForwarder
{
    public class FlightAwareBridge
    {
        private readonly APRSGateWay _gw;

        private readonly string _url;
        private readonly int _pollSecs;
        private readonly string _symbol;
        private readonly string _commentSuffix;
        private readonly string _nodePrefix;

        private Thread _thr;
        private volatile bool _running;

        private Dictionary<string, DateTime> _lastTx = new Dictionary<string, DateTime>();
        private readonly TimeSpan _minInterval = TimeSpan.FromSeconds(15);

        public FlightAwareBridge(APRSGateWay gw,
                                 string url, int pollSecs,
                                 string symbol, string commentSuffix,
                                 string nodePrefix)
        {
            _gw = gw;
            _url = (url == null || url.Length == 0) ? "http://127.0.0.1:8080/data/aircraft.json" : url;
            _pollSecs = (pollSecs > 0) ? pollSecs : 5;
            _symbol = (symbol != null && symbol.Length >= 2) ? symbol : "/>";
            _commentSuffix = (commentSuffix == null) ? "via FlightAware" : commentSuffix;
            _nodePrefix = (nodePrefix == null || nodePrefix.Length == 0) ? "AC" : nodePrefix;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thr = new Thread(new ThreadStart(Run));
            _thr.IsBackground = true;
            _thr.Name = "FlightAwareBridge";
            _thr.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_thr != null) _thr.Join(1000); } catch { }
            _thr = null;
        }

        private void Run()
        {
            Console.WriteLine("[FA] Starting poller: " + _url);
            while (_running)
            {
                try
                {
                    string json = HttpGetCompat(_url, 8000);
                    if (!string.IsNullOrEmpty(json))
                        HandleAircraftJson(json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[FA] " + ex.Message);
                }

                int ms = _pollSecs * 1000;
                while (_running && ms > 0) { Thread.Sleep(100); ms -= 100; }
            }
        }

        private static string HttpGet(string url, int timeoutMs)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.Timeout = timeoutMs;
            req.ReadWriteTimeout = timeoutMs;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (StreamReader sr = new StreamReader(s))
            {
                return sr.ReadToEnd();
            }
        }

		private static string HttpGetCompat(string url, int timeoutMs)
		{
			if (url != null && url.Length >= 6 &&
				(url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
			{
				try
				{
					Type t = Type.GetTypeFromProgID("WinHttp.WinHttpRequest.5.1");
					if (t == null)
					{
						Console.WriteLine("[HTTPS] WinHTTP not available on this system.");
						return null;
					}

					object req = Activator.CreateInstance(t);

					t.InvokeMember("SetProxy",
						System.Reflection.BindingFlags.InvokeMethod, null, req,
						new object[] { 1 });

					t.InvokeMember("Open",
						System.Reflection.BindingFlags.InvokeMethod, null, req,
						new object[] { "GET", url, false });

					t.InvokeMember("SetTimeouts",
						System.Reflection.BindingFlags.InvokeMethod, null, req,
						new object[] { timeoutMs, timeoutMs, timeoutMs, timeoutMs });

					t.InvokeMember("SetRequestHeader",
						System.Reflection.BindingFlags.InvokeMethod, null, req,
						new object[] { "User-Agent", "APRS-HTTPS-Bridge/1.0" });

					t.InvokeMember("Send",
						System.Reflection.BindingFlags.InvokeMethod, null, req, null);

					object statusObj = t.InvokeMember("Status",
						System.Reflection.BindingFlags.GetProperty, null, req, null);
					int status = (statusObj is int) ? (int)statusObj : 0;

					if (status < 200 || status >= 300)
					{
						object stxt = t.InvokeMember("StatusText",
							System.Reflection.BindingFlags.GetProperty, null, req, null);
						Console.WriteLine("[HTTPS] HTTP " + status.ToString() + " " + (stxt ?? ""));
						return null;
					}

					object respText = t.InvokeMember("ResponseText",
						System.Reflection.BindingFlags.GetProperty, null, req, null);
					return (respText == null) ? null : (string)respText;
				}
				catch (Exception ex)
				{
					Console.WriteLine("[HTTPS] " + ex.Message);
					return null;
				}
			}

			return HttpGet(url, timeoutMs);
		}

        private void HandleAircraftJson(string json)
        {
            Match arr = Regex.Match(json, "\"aircraft\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!arr.Success) return;
            string body = arr.Groups[1].Value;

            MatchCollection objs = Regex.Matches(body, "\\{[^\\}]*\\}");
            for (int i = 0; i < objs.Count; i++)
            {
                string a = objs[i].Value;

                string ident = ExtractFirst(a, "\"flight\"\\s*:\\s*\"([^\"]+)\"");
                if (string.IsNullOrEmpty(ident))
                    ident = ExtractFirst(a, "\"hex\"\\s*:\\s*\"([^\"]+)\"");
                if (string.IsNullOrEmpty(ident))
                    ident = _nodePrefix + Guid.NewGuid().ToString("N").Substring(0, 4);

                double lat, lon;
                if (!TryNum(a, "lat", out lat) || !TryNum(a, "lon", out lon))
                    continue; // need a fix

                int alt = 0;
                int tmp;
                if (TryInt(a, "alt_geom", out tmp)) alt = tmp;
                else if (TryInt(a, "alt_baro", out tmp)) alt = tmp;

                double gs = 0.0, trk = 0.0;
                TryNum(a, "gs", out gs);
                TryNum(a, "track", out trk);

                string src = ToLegalAprsCallsign(ident);

                StringBuilder comment = new StringBuilder();
                if (alt > 0) comment.Append("Alt ").Append(alt.ToString(CultureInfo.InvariantCulture)).Append("ft ");
                if (gs > 0)  comment.Append("Spd ").Append(gs.ToString("0", CultureInfo.InvariantCulture)).Append("kt ");
                if (trk > 0) comment.Append("Hdg ").Append(trk.ToString("0", CultureInfo.InvariantCulture)).Append(" ");
                comment.Append(_commentSuffix);
                string cmt = comment.ToString().Trim();

                DateTime last;
                if (_lastTx.TryGetValue(src, out last))
                {
                    if ((DateTime.UtcNow - last) < _minInterval) continue;
                }
                _lastTx[src] = DateTime.UtcNow;

                string line = BuildAprsPositionLine(src, lat, lon, _symbol, cmt);
                Console.WriteLine("[FA] " + line);
                _gw.TCPSend("ignored", 0, line);
            }
        }

        private static string ExtractFirst(string json, string pattern)
        {
            Match m = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            if (!m.Success) return null;
            return m.Groups[1].Value.Trim();
        }

        private static bool TryNum(string json, string key, out double value)
        {
            value = 0;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)",
                                  RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryInt(string json, string key, out int value)
        {
            value = 0;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)",
                                  RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static string ToLegalAprsCallsign(string s)
        {
            if (s == null) s = "NOCALL";
            s = s.ToUpperInvariant();
            s = Regex.Replace(s, @"[^A-Z0-9\-]", "");
            if (s.Length == 0) s = "NOCALL";
            string[] parts = s.Split('-');
            string baseCall = parts[0];
            if (baseCall.Length > 6) baseCall = baseCall.Substring(0, 6);
            int ssid = 0;
            if (parts.Length > 1)
            {
                int pssid;
                if (int.TryParse(parts[1], out pssid) && pssid >= 0 && pssid <= 15) ssid = pssid;
            }
            return (ssid > 0) ? (baseCall + "-" + ssid.ToString()) : baseCall;
        }

        private static void ToAprsLat(double lat, out string latStr, out string hemi)
        {
            hemi = (lat >= 0) ? "N" : "S";
            lat = Math.Abs(lat);
            int deg = (int)Math.Floor(lat);
            double min = (lat - deg) * 60.0;
            latStr = string.Format(CultureInfo.InvariantCulture, "{0:00}{1:00.00}", deg, min);
        }

        private static void ToAprsLon(double lon, out string lonStr, out string hemi)
        {
            hemi = (lon >= 0) ? "E" : "W";
            lon = Math.Abs(lon);
            int deg = (int)Math.Floor(lon);
            double min = (lon - deg) * 60.0;
            lonStr = string.Format(CultureInfo.InvariantCulture, "{0:000}{1:00.00}", deg, min);
        }

        private static string BuildAprsPositionLine(string callsign, double lat, double lon, string symbol, string comment)
        {
            string latStr, latH, lonStr, lonH;
            ToAprsLat(lat, out latStr, out latH);
            ToAprsLon(lon, out lonStr, out lonH);

            string sym = (symbol != null && symbol.Length >= 2) ? symbol : "/>";
            string body = "!" + latStr + latH + sym[0] + lonStr + lonH + sym[1]
                          + (string.IsNullOrEmpty(comment) ? "" : comment);
            return callsign + ">APRS,TCPIP*:" + body;
        }
    }
}
