using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace APRSForwarder
{
    public class MeshMqttBridge
    {
        private readonly APRSGateWay _gw;

        private readonly string _host;
        private readonly int _port;
        private readonly bool _useTls;
        private readonly bool _tlsIgnoreErrors;
        private readonly string _user;
        private readonly string _pass;
        private readonly string _topic;
        private readonly int _keepAlive;

        private readonly string _nodePrefix;
        private readonly string _symbol;
        private readonly string _commentSuffix;

        private Thread _thr;
        private volatile bool _running;

        public MeshMqttBridge(
            APRSGateWay gw,
            string host, int port,
            bool useTls, bool tlsIgnoreErrors,
            string user, string pass,
            string topic, int keepAliveSecs,
            string nodePrefix, string symbol, string commentSuffix)
        {
            _gw = gw;
            _host = IsNullOrWhiteSpace(host) ? "mqtt.meshtastic.org" : host;
            _port = port > 0 ? port : (useTls ? 8883 : 1883);
            _useTls = useTls;
            _tlsIgnoreErrors = tlsIgnoreErrors;
            _user = (user == null) ? "meshdev" : user;
            _pass = (pass == null) ? "large4cats" : pass;
            _topic = IsNullOrWhiteSpace(topic) ? "msh/US/#" : topic;
            _keepAlive = (keepAliveSecs > 0) ? keepAliveSecs : 60;
            _nodePrefix = IsNullOrWhiteSpace(nodePrefix) ? "MT0XYZ" : nodePrefix;
            _symbol = IsNullOrWhiteSpace(symbol) ? "/[" : symbol;
            _commentSuffix = IsNullOrWhiteSpace(commentSuffix) ? "via Meshtastic" : commentSuffix;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _thr = new Thread(new ThreadStart(Run));
            _thr.IsBackground = true;
            _thr.Name = "MeshMqttBridge";
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
            while (_running)
            {
                try
                {
                    using (TcpClient tcp = new TcpClient())
                    {
                        tcp.Connect(_host, _port);

                        Stream netStream = tcp.GetStream();
                        if (_useTls)
                        {
                            SslStream ssl = new SslStream(
                                netStream,
                                false,
                                delegate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errs)
                                {
                                    if (!_tlsIgnoreErrors) return errs == SslPolicyErrors.None;
                                    return true;
                                });
                            ssl.AuthenticateAsClient(_host);
                            netStream = ssl;
                        }

                        netStream.ReadTimeout = 15000;
                        netStream.WriteTimeout = 15000;

                        string clientId = "APRSGW-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                        SendConnect(netStream, clientId, _keepAlive, _user, _pass);

                        Packet pkt = ReadPacket(netStream);
                        if (pkt.Type != 0x20 || pkt.Payload.Length < 2 || pkt.Payload[1] != 0x00)
                            throw new Exception("MQTT CONNACK failed");
						Console.WriteLine("[MQTT] Connected to " + _host + ":" + _port + " (TLS=" + _useTls + ")");

                        SendSubscribe(netStream, 1, _topic, 0);

                        pkt = ReadPacket(netStream);
                        if (pkt.Type != 0x90)
                            throw new Exception("MQTT SUBACK not received");
						Console.WriteLine("[MQTT] Subscribed to '" + _topic + "'");

                        DateTime lastTx = DateTime.UtcNow;
                        while (_running && tcp.Connected)
                        {
                            if ((DateTime.UtcNow - lastTx).TotalSeconds > Math.Max(10, _keepAlive / 2))
                            {
                                SendPingReq(netStream);
                                lastTx = DateTime.UtcNow;
                            }

                            if (!netStream.CanRead)
                            {
                                Thread.Sleep(200);
                                continue;
                            }

                            netStream.ReadTimeout = 1000;
                            Packet p;
                            try
                            {
                                p = ReadPacket(netStream);
                            }
                            catch (IOException)
                            {
                                continue;
                            }
                            catch (Exception)
                            {
                                break;
                            }

							if (p.Type == 0x30 || (p.Type & 0xF0) == 0x30)
							{
								if (p.Payload == null || p.Payload.Length < 2) continue;
								int tlen = (p.Payload[0] << 8) | p.Payload[1];
								if (2 + tlen > p.Payload.Length) continue;

								string topic = Encoding.UTF8.GetString(p.Payload, 2, tlen);
								int skip = 2 + tlen;
								if (skip < 0 || skip > p.Payload.Length) continue;

								string msg = Encoding.UTF8.GetString(p.Payload, skip, p.Payload.Length - skip);
								HandlePublish(msg, topic);
							}
                            else if (p.Type == 0xD0)
                            {
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                }

                if (_running) Thread.Sleep(3000);
            }
        }

        private static void WriteUInt16BE(Stream s, ushort value)
        {
            s.WriteByte((byte)((value >> 8) & 0xFF));
            s.WriteByte((byte)(value & 0xFF));
        }

        private static ushort ReadUInt16BE(byte[] buf, int offset)
        {
            return (ushort)((buf[offset] << 8) | buf[offset + 1]);
        }

		private static bool TryExtractPositionFromJson(string json, out double lat, out double lon, out int? alt)
		{
			lat = 0; lon = 0; alt = null;
			if (string.IsNullOrEmpty(json)) return false;

			Match mLat = Regex.Match(json, "\"latitude\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			Match mLon = Regex.Match(json, "\"longitude\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);

			if (!mLat.Success || !mLon.Success)
			{
				mLat = Regex.Match(json, "\"lat\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
				mLon = Regex.Match(json, "\"lon\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			}

			bool usedScaled = false;
			if (!mLat.Success || !mLon.Success)
			{
				Match mLatI = Regex.Match(json, "\"latitude_i\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);
				Match mLonI = Regex.Match(json, "\"longitude_i\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);
				if (mLatI.Success && mLonI.Success)
				{
					long li, loi;
					if (long.TryParse(mLatI.Groups[1].Value, out li) && long.TryParse(mLonI.Groups[1].Value, out loi))
					{
						lat = li / 10000000.0;
						lon = loi / 10000000.0;
						usedScaled = true;
					}
				}
			}

			if (!usedScaled)
			{
				if (!mLat.Success || !mLon.Success) return false;

				if (!double.TryParse(mLat.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lat)) return false;
				if (!double.TryParse(mLon.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out lon)) return false;
			}

			Match mAlt = Regex.Match(json, "\"altitude(M)?\"\\s*:\\s*(-?\\d+)", RegexOptions.CultureInvariant);
			if (mAlt.Success)
			{
				int a;
				if (int.TryParse(mAlt.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out a))
					alt = a;
			}
			return true;
		}

        private static string ExtractLongNameFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string s;
            s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"longName\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;
			
            return null;
        }
        private static string ExtractShortNameFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string s;
            s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"shortName\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;
			
            return null;
        }
        private static string ExtractSenderFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string s;
            s = ExtractFirstString(json, "\"sender\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;
			
            return null;
        }

        private static string ExtractCallsignFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string s;
            s = ExtractFirstString(json, "\"from\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            s = ExtractFirstString(json, "\"sender\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            s = ExtractFirstString(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"longName\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"shortName\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (!string.IsNullOrEmpty(s)) return s;

            return null;
        }

        private static string ExtractBatteryFromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            Match m = Regex.Match(json, "\"battery(Level)?\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                double v;
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                    return v.ToString("0", CultureInfo.InvariantCulture) + "%";
            }

            m = Regex.Match(json, "\"deviceMetrics\"\\s*:\\s*\\{[^}]*?\"battery_level\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
            if (m.Success)
            {
                double v2;
                if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v2))
                    return v2.ToString("0", CultureInfo.InvariantCulture) + "%";
            }
            return null;
        }

		private static bool TryExtractTelemetryFromJson(
			string json,
			out string battPct, out string volt, out string tempC, out string humPct,
			out string pressHPa, out string rssi, out string snr, out string airUtil)
		{
			battPct = volt = tempC = humPct = pressHPa = rssi = snr = airUtil = null;
			if (string.IsNullOrEmpty(json)) return false;

			Match m = Regex.Match(json, "\"battery(Level)?\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) battPct = ToNumStr0(m.Groups[2].Value) + "%";

			m = Regex.Match(json, "\"voltage\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) volt = ToNumStr2(m.Groups[1].Value) + "V";

			m = Regex.Match(json, "\"temperature(C)?\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (!m.Success) m = Regex.Match(json, "\"temperature\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) tempC = ToNumStr1(m.Groups[m.Groups.Count - 1].Value) + "C";

			m = Regex.Match(json, "\"hum(idity)?\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) humPct = ToNumStr0(m.Groups[m.Groups.Count - 1].Value) + "%";

			m = Regex.Match(json, "\"(barometric)?pressure\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) pressHPa = ToNumStr0(m.Groups[m.Groups.Count - 1].Value) + "hPa";

			m = Regex.Match(json, "\"(rx)?rssi\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) rssi = ToNumStr0(m.Groups[m.Groups.Count - 1].Value) + "dBm";

			m = Regex.Match(json, "\"(rx)?snr\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) snr = ToNumStr1(m.Groups[m.Groups.Count - 1].Value) + "dB";

			m = Regex.Match(json, "\"air(Util(Tx)?)\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success) airUtil = ToNumStr1(m.Groups[m.Groups.Count - 1].Value) + "%";

			return battPct != null || volt != null || tempC != null || humPct != null ||
				   pressHPa != null || rssi != null || snr != null || airUtil != null;
		}

		private static string ToNumStr0(string s)
		{
			double d;
			if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
				return Math.Round(d).ToString("0", CultureInfo.InvariantCulture);
			return s;
		}
		private static string ToNumStr1(string s)
		{
			double d;
			if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
				return d.ToString("0.0", CultureInfo.InvariantCulture);
			return s;
		}
		private static string ToNumStr2(string s)
		{
			double d;
			if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
				return d.ToString("0.00", CultureInfo.InvariantCulture);
			return s;
		}

		private Dictionary<string, DateTime> _lastInfoAnnounce = new Dictionary<string, DateTime>();

		private static bool TryExtractNodeInfoFromJson(string json, out string name, out string fw, out string hw)
		{
			name = fw = hw = null;
			if (string.IsNullOrEmpty(json)) return false;

			string s = ExtractFirstString(json, "\"longName\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(s)) s = ExtractFirstString(json, "\"shortName\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(s)) s = ExtractFirstString(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
			name = s;

			fw = ExtractFirstString(json, "\"firmware\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(fw)) fw = ExtractFirstString(json, "\"fw\"\\s*:\\s*\"([^\"]+)\"");

			hw = ExtractFirstString(json, "\"hw(Model|Version)?\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(hw)) hw = ExtractFirstString(json, "\"hardware\"\\s*:\\s*\"([^\"]+)\"");

			return name != null || fw != null || hw != null;
		}

		private static string ChannelFromTopic(string topic)
		{
			if (string.IsNullOrEmpty(topic)) return null;
			string[] parts = topic.Split('/');
			int i;
			for (i = 0; i < parts.Length; i++)
				if (string.Compare(parts[i], "json", StringComparison.OrdinalIgnoreCase) == 0)
					return (i + 1 < parts.Length) ? parts[i + 1] : null;
			return null;
		}

		private static bool TryExtractTextFromJson(string json, out string text, out string to)
		{
			text = null; to = null;
			if (string.IsNullOrEmpty(json)) return false;

			Match mt = Regex.Match(json, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);
			if (!mt.Success) return false;

			text = mt.Groups[1].Value;
			text = text.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ");
			text = SanitizeAscii(text, 67);

			string[] destKeys = new string[] { "to", "toId", "toCall", "dest", "destination" };
			int i;
			for (i = 0; i < destKeys.Length; i++)
			{
				string pat = "\"" + destKeys[i] + "\"\\s*:\\s*\"([^\"]+)\"";
				Match md = Regex.Match(json, pat, RegexOptions.CultureInvariant);
				if (md.Success)
				{
					to = ToLegalAprsCallsign(md.Groups[1].Value);
					break;
				}
			}

			if (!string.IsNullOrEmpty(to))
			{
				if (!Regex.IsMatch(to, "^[A-Z0-9]{1,6}(-([0-9]|1[0-5]))?$"))
					to = null;
			}
			return !string.IsNullOrEmpty(text);
		}

        private static string ExtractFirstString(string json, string pattern)
        {
            Match m = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            if (!m.Success) return null;
            return m.Groups[1].Value;
        }

        private static string SafeSuffixFromJson(string json)
        {
            string id = ExtractFirstString(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");
            if (string.IsNullOrEmpty(id)) id = Guid.NewGuid().ToString("N");

            string digits = Regex.Replace(id, "\\D", "");
            int n;
            if (int.TryParse(digits, out n) && n > 0 && n <= 15) return "-" + n.ToString(CultureInfo.InvariantCulture);

            string two = id.ToUpperInvariant();
            if (two.Length >= 2) two = two.Substring(0, 2);
            return "-" + two;
        }

        private void HandlePublish(string msg, string topic)
        {
            try
            {
                if (string.IsNullOrEmpty(msg)) return;

				string callsign = ExtractCallsignFromJson(msg);
				if (string.IsNullOrEmpty(callsign)) callsign = _nodePrefix + SafeSuffixFromJson(msg);

                callsign = ExtractSenderFromJson (msg); // ToLegalAprsCallsign(callsign);

				string _comment_all = "";

				string nName, nFw, nHw;
				if (TryExtractNodeInfoFromJson(msg, out nName, out nFw, out nHw))
				{
					string ch = ChannelFromTopic(topic);
					string info = "Node:" + (nName ?? "-");
					if (!string.IsNullOrEmpty(nFw)) info += " FW:" + nFw;
					if (!string.IsNullOrEmpty(nHw)) info += " HW:" + nHw;
					if (!string.IsNullOrEmpty(ch))  info += " Ch:" + ch;

					_comment_all += " "+ info;
				}

				string battPct, volt, tempC, humPct, pressHPa, rssi, snr, airUtil;
				bool hasTel = TryExtractTelemetryFromJson(msg, out battPct, out volt, out tempC, out humPct, out pressHPa, out rssi, out snr, out airUtil);
				StringBuilder tel = new StringBuilder();
				if (hasTel)
				{
					if (!string.IsNullOrEmpty(battPct)) tel.Append("Batt ").Append(battPct).Append(" ");
					if (!string.IsNullOrEmpty(volt))    tel.Append("V").Append(volt).Append(" ");
					if (!string.IsNullOrEmpty(tempC))   tel.Append("T").Append(tempC).Append(" ");
					if (!string.IsNullOrEmpty(humPct))  tel.Append("H").Append(humPct).Append(" ");
					if (!string.IsNullOrEmpty(pressHPa))tel.Append("P").Append(pressHPa).Append(" ");
					if (!string.IsNullOrEmpty(snr))     tel.Append("SNR").Append(snr).Append(" ");
					if (!string.IsNullOrEmpty(rssi))    tel.Append("RSSI").Append(rssi).Append(" ");
					if (!string.IsNullOrEmpty(airUtil)) tel.Append("Air").Append(airUtil).Append(" ");
				}

				string telSnippet = tel.ToString().Trim();
				double lat, lon;
				int? alt;
				if (TryExtractPositionFromJson(msg, out lat, out lon, out alt))
				{
					string comment = BuildComment(null, alt, "");
					if (!string.IsNullOrEmpty(telSnippet))
					{
						string merged = comment;
						if (!IsNullOrWhiteSpace(merged)) merged += " ";
						merged += telSnippet;
						if (merged.Length > 70) merged = merged.Substring(0, 70);
						comment = merged;
					}

					_comment_all += " "+ comment;
				}
				else if (!string.IsNullOrEmpty(telSnippet))
				{
					_comment_all += " "+ SanitizeAscii(telSnippet, 67);
				}

				string tmsg, dest;
				if (TryExtractTextFromJson(msg, out tmsg, out dest))
				{
					_comment_all += " "+ dest +" "+ tmsg;
				}

				string line = BuildAprsPositionLine(callsign, lat, lon, _symbol, _comment_all +" "+ _commentSuffix);
				string prev = _comment_all.Length > 255 ? _comment_all.Substring(0, 255) + "..." : _comment_all;
				Console.WriteLine("[MQTT] "+ msg +" "+ prev);
				_gw.TCPSend("ignored", 0, line);
            }
            catch (Exception ex)
            {
            }
        }

        private struct Packet { public byte Type; public byte[] Payload; }

        private static void SendConnect(Stream s, string clientId, int keepAlive, string user, string pass)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                WriteString(ms, "MQTT");
                ms.WriteByte(0x04);

                byte flags = 0x02;
                if (!string.IsNullOrEmpty(user)) flags |= 0x80;
                if (!string.IsNullOrEmpty(pass)) flags |= 0x40;
                ms.WriteByte(flags);

                WriteUInt16BE(ms, (ushort)keepAlive);

                WriteString(ms, clientId);
                if (!string.IsNullOrEmpty(user)) WriteString(ms, user);
                if (!string.IsNullOrEmpty(pass)) WriteString(ms, pass);

                WriteFixedHeaderAndBody(s, 0x10, ms.ToArray());
            }
        }

        private static void SendSubscribe(Stream s, ushort packetId, string topic, byte qos)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                WriteUInt16BE(ms, packetId);
                WriteString(ms, topic);
                ms.WriteByte(qos);

                WriteFixedHeaderAndBody(s, 0x82, ms.ToArray());
            }
        }

        private static void SendPingReq(Stream s)
        {
            s.WriteByte(0xC0);
            s.WriteByte(0x00);
            s.Flush();
        }

        private static Packet ReadPacket(Stream s)
        {
            int h = s.ReadByte();
            if (h < 0) throw new EndOfStreamException();

            int remLen = ReadVarLen(s);

            byte[] payload = new byte[remLen];
            int read = 0;
            while (read < remLen)
            {
                int r = s.Read(payload, read, remLen - read);
                if (r <= 0) throw new EndOfStreamException();
                read += r;
            }

            Packet pk;
            pk.Type = (byte)h;
            pk.Payload = payload;
            return pk;
        }

        private static int ReadVarLen(Stream s)
        {
            int multiplier = 1;
            int value = 0;
            int loops = 0;
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0) throw new EndOfStreamException();
                value += (b & 127) * multiplier;
                if ((b & 128) == 0) break;
                multiplier *= 128;
                loops++;
                if (loops > 4) throw new InvalidDataException("Malformed Remaining Length");
            }
            return value;
        }

        private static void WriteFixedHeaderAndBody(Stream s, byte type, byte[] body)
        {
            s.WriteByte(type);
            WriteVarLen(s, body.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        private static void WriteVarLen(Stream s, int value)
        {
            do
            {
                int digit = value % 128;
                value /= 128;
                if (value > 0) digit |= 128;
                s.WriteByte((byte)digit);
            } while (value > 0);
        }

        private static void WriteString(Stream s, string str)
        {
            byte[] b = Encoding.UTF8.GetBytes(str);
            WriteUInt16BE(s, (ushort)b.Length);
            s.Write(b, 0, b.Length);
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

        private static string ToLegalAprsCallsign(string s)
        {
            s = (s ?? "NOCALL").ToUpperInvariant();
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

		private static string SanitizeAscii(string s, int maxLen)
		{
			if (s == null) return "";
			StringBuilder sb = new StringBuilder(s.Length);
			int i;
			for (i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (c >= 32 && c <= 126) sb.Append(c);
				else sb.Append(' ');
				if (sb.Length >= maxLen) break;
			}
			string outp = sb.ToString().Trim();
			return outp;
		}

		private static string BuildAprsMessageLine(string from, string to, string text)
		{
			string dest = (to ?? "NOCALL").ToUpperInvariant();
			if (dest.Length > 9) dest = dest.Substring(0, 9);
			while (dest.Length < 9) dest += " ";
			return from + ">APRS,TCPIP*::" + dest + ":" + text;
		}

		private static string BuildAprsStatusLine(string from, string text)
		{
			return from + ">APRS,TCPIP*:>" + text;
		}

        private static string BuildAprsPositionLine(string callsign, double lat, double lon, string symbol, string comment)
        {
            string latStr, latH, lonStr, lonH;
            ToAprsLat(lat, out latStr, out latH);
            ToAprsLon(lon, out lonStr, out lonH);

			string sym = (symbol != null && symbol.Length >= 2) ? symbol : "/[";
			string body = "!" + latStr + latH + sym[0] + lonStr + lonH + sym[1]
						  + (IsNullOrWhiteSpace(comment) ? "" : comment);
            return callsign + ">APRS,TCPIP*:" + body;
        }

        private static string BuildComment(string batt, int? alt, string suffix)
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrEmpty(batt)) sb.Append("Batt " + batt + " ");
            if (alt.HasValue) sb.Append("Alt " + alt.Value.ToString(CultureInfo.InvariantCulture) + "m ");
            if (!string.IsNullOrEmpty(suffix)) sb.Append(suffix);
            return sb.ToString().Trim();
        }

        private static bool IsNullOrWhiteSpace(string s)
        {
            if (s == null) return true;
            for (int i = 0; i < s.Length; i++)
            {
                if (!char.IsWhiteSpace(s[i])) return false;
            }
            return true;
        }

        private static int TopicSkipLength(byte[] payload)
        {
            if (payload == null || payload.Length < 2) return 0;
            int len = ReadUInt16BE(payload, 0);
            int skip = 2 + len;
            if (skip < 0 || skip > payload.Length) return 0;
            return skip;
        }
    }
}
