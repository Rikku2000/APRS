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
    public class VesselFinderBridge
    {
        private readonly APRSGateWay _gw;

        private readonly string _jsonUrl;
        private readonly int _pollSecs;
        private readonly string _symbol;
        private readonly string _commentSuffix;
        private readonly string _nodePrefix;
        private readonly TimeSpan _minInterval;

        private Thread _thr;
        private volatile bool _running;

        private readonly Dictionary<string, DateTime> _lastTx = new Dictionary<string, DateTime>();

        public VesselFinderBridge(APRSGateWay gw,
                                  string jsonUrl, int pollSecs,
                                  string symbol, string commentSuffix,
                                  string nodePrefix, int minTxSec)
        {
            _gw = gw;
            _jsonUrl = jsonUrl;
            _pollSecs = (pollSecs > 0) ? pollSecs : 10;
            _symbol = (symbol != null && symbol.Length >= 2) ? symbol : "\\>";
            _commentSuffix = (commentSuffix == null) ? "via AIS" : commentSuffix;
            _nodePrefix = (nodePrefix == null || nodePrefix.Length == 0) ? "SHIP" : nodePrefix;
            _minInterval = TimeSpan.FromSeconds((minTxSec > 0) ? minTxSec : 30);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thr = new Thread(new ThreadStart(Run));
            _thr.IsBackground = true;
            _thr.Name = "VesselFinderBridge";
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
            if (string.IsNullOrEmpty(_jsonUrl))
            {
                Console.WriteLine("[AIS] No json_url configured.");
                return;
            }

            Console.WriteLine("[AIS] Polling " + _jsonUrl);
            while (_running)
            {
                try
                {
                    string json = HttpGetCompat(_jsonUrl, 12000);
                    if (!string.IsNullOrEmpty(json))
                        HandleJson(json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[AIS] " + ex.Message);
                }

                int ms = _pollSecs * 1000;
                while (_running && ms > 0) { Thread.Sleep(100); ms -= 100; }
            }
        }

        private void HandleJson(string json)
        {
            string body = json;

            MatchCollection objs = Regex.Matches(body, "\\{[^\\{\\}]*\\}");
            int i;
            for (i = 0; i < objs.Count; i++)
            {
                string o = objs[i].Value;
                ProcessVesselObject(o);
            }
        }

        private void ProcessVesselObject(string o)
        {
            string ident =
                ExtractFirst(o, "\"callsign\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(ident))
                ident = ExtractFirst(o, "\"shipname\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(ident))
                ident = ExtractFirst(o, "\"name\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(ident))
                ident = ExtractFirst(o, "\"mmsi\"\\s*:\\s*\"?([0-9]{6,9})\"?");
            if (string.IsNullOrEmpty(ident))
                ident = ExtractFirst(o, "\"imo\"\\s*:\\s*\"?([0-9]{6,9})\"?");

            if (string.IsNullOrEmpty(ident))
                ident = _nodePrefix + Guid.NewGuid().ToString("N").Substring(0, 4);

            double lat, lon;
            if (!TryNum(o, "lat", out lat) && !TryNum(o, "latitude", out lat)) return;
            if (!TryNum(o, "lon", out lon) && !TryNum(o, "lng", out lon) && !TryNum(o, "longitude", out lon)) return;

            double sog = 0.0, cog = 0.0, hdg = 0.0;
            TryNum(o, "sog", out sog);
            TryNum(o, "speed", out sog);
            TryNum(o, "cog", out cog);
            TryNum(o, "course", out cog);
            TryNum(o, "heading", out hdg);

            string status =
                ExtractFirst(o, "\"navstatus\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(status))
                status = ExtractFirst(o, "\"status\"\\s*:\\s*\"([^\"]+)\"");

            string dest = ExtractFirst(o, "\"destination\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(dest))
                dest = ExtractFirst(o, "\"dest\"\\s*:\\s*\"([^\"]+)\"");

            string src = ToLegalAprsCallsign(ident);

            DateTime last;
            if (_lastTx.TryGetValue(src, out last))
            {
                if ((DateTime.UtcNow - last) < _minInterval) return;
            }
            _lastTx[src] = DateTime.UtcNow;

            StringBuilder cmt = new StringBuilder();
            if (sog > 0) cmt.Append("Spd ").Append(sog.ToString("0", CultureInfo.InvariantCulture)).Append("kn ");
            if (cog > 0) cmt.Append("Crse ").Append(cog.ToString("0", CultureInfo.InvariantCulture)).Append(" ");
            if (hdg > 0) cmt.Append("Hdg ").Append(hdg.ToString("0", CultureInfo.InvariantCulture)).Append(" ");
            if (!string.IsNullOrEmpty(status)) cmt.Append(SanitizeAscii(status, 18)).Append(' ');
            if (!string.IsNullOrEmpty(dest))   cmt.Append("To ").Append(SanitizeAscii(dest, 18)).Append(' ');
            cmt.Append(_commentSuffix);
            string comment = cmt.ToString().Trim();

            string line = BuildAprsPositionLine(src, lat, lon, _symbol, comment);
            Console.WriteLine("[AIS] " + line);
            _gw.TCPSend("ignored", 0, line);
        }

        private static string HttpGetCompat(string url, int timeoutMs)
        {
            if (url != null && url.Length >= 6 &&
                (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    Type t = Type.GetTypeFromProgID("WinHttp.WinHttpRequest.5.1");
                    if (t == null) { Console.WriteLine("[HTTPS] WinHTTP not available."); return null; }
                    object req = Activator.CreateInstance(t);
                    t.InvokeMember("SetProxy", System.Reflection.BindingFlags.InvokeMethod, null, req, new object[] { 1 });
                    t.InvokeMember("Open", System.Reflection.BindingFlags.InvokeMethod, null, req, new object[] { "GET", url, false });
                    t.InvokeMember("SetTimeouts", System.Reflection.BindingFlags.InvokeMethod, null, req, new object[] { timeoutMs, timeoutMs, timeoutMs, timeoutMs });
                    t.InvokeMember("SetRequestHeader", System.Reflection.BindingFlags.InvokeMethod, null, req, new object[] { "User-Agent", "APRS-AIS-Bridge/1.0" });
                    t.InvokeMember("Send", System.Reflection.BindingFlags.InvokeMethod, null, req, null);
                    object statusObj = t.InvokeMember("Status", System.Reflection.BindingFlags.GetProperty, null, req, null);
                    int status = (statusObj is int) ? (int)statusObj : 0;
                    if (status < 200 || status >= 300)
                    {
                        object stxt = t.InvokeMember("StatusText", System.Reflection.BindingFlags.GetProperty, null, req, null);
                        Console.WriteLine("[HTTPS] HTTP " + status.ToString() + " " + (stxt ?? ""));
                        return null;
                    }
                    object respText = t.InvokeMember("ResponseText", System.Reflection.BindingFlags.GetProperty, null, req, null);
                    return (respText == null) ? null : (string)respText;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[HTTPS] " + ex.Message);
                    return null;
                }
            }

            try
            {
                System.Net.ServicePointManager.Expect100Continue = false;
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.Proxy = null;
                req.KeepAlive = false;
                req.ProtocolVersion = System.Net.HttpVersion.Version10;
                req.UserAgent = "APRS-AIS-Bridge/1.0";
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                using (Stream s = resp.GetResponseStream())
                using (StreamReader sr = new StreamReader(s))
                {
                    return sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[AIS] HttpGet error: " + ex.Message);
                return null;
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
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
            if (!m.Success) return false;
            return double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string SanitizeAscii(string s, int maxLen)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            int i;
            for (i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c >= 32 && c <= 126) sb.Append(c); else sb.Append(' ');
                if (sb.Length >= maxLen) break;
            }
            string outp = sb.ToString().Trim();
            return outp;
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

            string sym = (symbol != null && symbol.Length >= 2) ? symbol : "\\>";
            string body = "!" + latStr + latH + sym[0] + lonStr + lonH + sym[1]
                          + (string.IsNullOrEmpty(comment) ? "" : comment);
            return callsign + ">APRS,TCPIP*:" + body;
        }
    }
}
