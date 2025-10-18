using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace APRSForwarder
{
    public class Dump1090Bridge
    {
        private readonly APRSGateWay _gw;
        private readonly string _jsonUrl;
        private readonly int _pollSecs;
        private readonly string _sbsHost;
        private readonly int _sbsPort;
        private readonly string _symbol;
        private readonly string _commentSuffix;
        private readonly string _nodePrefix;
        private readonly TimeSpan _minInterval;

        private Thread _thr;
        private volatile bool _running;

        private readonly Dictionary<string, DateTime> _lastTx = new Dictionary<string, DateTime>();

        public Dump1090Bridge(APRSGateWay gw,
                              string jsonUrl, int pollSecs,
                              string sbsHost, int sbsPort,
                              string symbol, string commentSuffix,
                              string nodePrefix, int minTxSec)
        {
            _gw = gw;
            _jsonUrl = jsonUrl;
            _pollSecs = (pollSecs > 0) ? pollSecs : 5;
            _sbsHost = sbsHost;
            _sbsPort = (sbsPort > 0) ? sbsPort : 30003;
            _symbol = (symbol != null && symbol.Length >= 2) ? symbol : "/>";
            _commentSuffix = string.IsNullOrEmpty(commentSuffix) ? "via dump1090" : commentSuffix;
            _nodePrefix = string.IsNullOrEmpty(nodePrefix) ? "AC" : nodePrefix;
            _minInterval = TimeSpan.FromSeconds((minTxSec > 0) ? minTxSec : 15);
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thr = new Thread(new ThreadStart(Run));
            _thr.IsBackground = true;
            _thr.Name = "Dump1090Bridge";
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
            if (!string.IsNullOrEmpty(_sbsHost))
            {
                Console.WriteLine("[DUMP1090] Connecting SBS-1 " + _sbsHost + ":" + _sbsPort);
                RunSbs();
                return;
            }

            string url = string.IsNullOrEmpty(_jsonUrl) ? "http://127.0.0.1:8080/data/aircraft.json" : _jsonUrl;
            Console.WriteLine("[DUMP1090] Polling JSON " + url);
            while (_running)
            {
                try
                {
                    string json = HttpGet(url, 8000);
                    if (!string.IsNullOrEmpty(json))
                        HandleJson(json);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DUMP1090] " + ex.Message);
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

        private void HandleJson(string json)
        {
            Match arr = Regex.Match(json, "\"aircraft\"\\s*:\\s*\\[(.*)\\]", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            if (!arr.Success) return;
            string body = arr.Groups[1].Value;

            MatchCollection objs = Regex.Matches(body, "\\{[^\\}]*\\}");
            for (int i = 0; i < objs.Count; i++)
            {
                string a = objs[i].Value;
                ProcessJsonAircraft(a);
            }
        }

        private void ProcessJsonAircraft(string a)
        {
            string ident = ExtractFirst(a, "\"flight\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(ident)) ident = ExtractFirst(a, "\"hex\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(ident)) ident = _nodePrefix + Guid.NewGuid().ToString("N").Substring(0, 4);

            double lat, lon;
            if (!TryNum(a, "lat", out lat) || !TryNum(a, "lon", out lon)) return;

            int alt = 0, vr = 0;
            int tmp;
            if (TryInt(a, "alt_geom", out tmp)) alt = tmp; else if (TryInt(a, "alt_baro", out tmp)) alt = tmp;
            TryInt(a, "baro_rate", out vr);

            double spd = 0.0, trk = 0.0;
            TryNum(a, "gs", out spd);
            TryNum(a, "track", out trk);

            string squawk = ExtractFirst(a, "\"squawk\"\\s*:\\s*\"([^\"]+)\"");
            bool emerg = Regex.IsMatch(a, "\"emergency\"\\s*:\\s*true", RegexOptions.CultureInvariant);

            EmitAprs(ident, lat, lon, alt, spd, trk, vr, squawk, emerg);
        }

        private void RunSbs()
        {
            while (_running)
            {
                try
                {
                    using (TcpClient tcp = new TcpClient())
                    {
                        tcp.Connect(_sbsHost, _sbsPort);
                        Console.WriteLine("[DUMP1090] SBS connected");
                        using (NetworkStream ns = tcp.GetStream())
                        using (StreamReader sr = new StreamReader(ns))
                        {
                            string line;
                            while (_running && (line = sr.ReadLine()) != null)
                            {
                                string[] f = line.Split(',');
                                if (f.Length < 22) continue;
                                if (!string.Equals(f[0], "MSG", StringComparison.OrdinalIgnoreCase)) continue;

                                string type = f[1];
                                string ident = f[10];
                                if (string.IsNullOrEmpty(ident)) ident = f[4];

                                double lat, lon;
                                if (!double.TryParse(f[14], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)) continue;
                                if (!double.TryParse(f[15], NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) continue;

                                int alt = 0, vr = 0;
                                int.TryParse(f[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out alt);

                                double spd = 0.0, trk = 0.0;
                                double.TryParse(f[12], NumberStyles.Float, CultureInfo.InvariantCulture, out spd);
                                double.TryParse(f[13], NumberStyles.Float, CultureInfo.InvariantCulture, out trk);

                                EmitAprs(ident, lat, lon, alt, spd, trk, vr, null, false);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DUMP1090] " + ex.Message);
                }
                if (_running) Thread.Sleep(2000);
            }
        }

        private void EmitAprs(string ident, double lat, double lon, int altFt, double spdKt, double trkDeg, int vrFpm, string squawk, bool emergency)
        {
            string src = ToLegalAprsCallsign(ident);
            DateTime last;
            if (_lastTx.TryGetValue(src, out last))
            {
                if ((DateTime.UtcNow - last) < _minInterval) return;
            }
            _lastTx[src] = DateTime.UtcNow;

            StringBuilder cmt = new StringBuilder();
            if (altFt > 0) cmt.Append("Alt ").Append(altFt.ToString(CultureInfo.InvariantCulture)).Append("ft ");
            if (spdKt > 0) cmt.Append("Spd ").Append(spdKt.ToString("0", CultureInfo.InvariantCulture)).Append("kt ");
            if (trkDeg > 0) cmt.Append("Hdg ").Append(trkDeg.ToString("0", CultureInfo.InvariantCulture)).Append(" ");
            if (vrFpm != 0) cmt.Append("VR ").Append(vrFpm.ToString(CultureInfo.InvariantCulture)).Append("fpm ");
            if (!string.IsNullOrEmpty(squawk)) cmt.Append("Sq ").Append(squawk).Append(' ');
            if (emergency) cmt.Append("EMER ");
            cmt.Append(_commentSuffix);

            string line = BuildAprsPositionLine(src, lat, lon, _symbol, cmt.ToString().Trim());
            Console.WriteLine("[DUMP1090] " + line);
            _gw.TCPSend("ignored", 0, line);
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

        private static bool TryInt(string json, string key, out int value)
        {
            value = 0;
            Match m = Regex.Match(json, "\"" + key + "\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);
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
