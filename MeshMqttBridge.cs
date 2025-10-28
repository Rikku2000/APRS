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
		private readonly int _threadSleep;

        private Thread _thr;
        private volatile bool _running;

		private class NodeInfo { public string Name; public string Fw; public string Hw; public string Model; public string Chan; public DateTime Ts; }
		private class NodeText { public string Text; public string To; public DateTime Ts; }
		private class NodePos { public double Lat; public double Lon; public int? Alt; public DateTime Ts; }
		private class NodeTel { public string Snippet; public DateTime Ts; }

		private Dictionary<string, NodeInfo> _infoCache = new Dictionary<string, NodeInfo>();
		private Dictionary<string, NodeText> _textCache = new Dictionary<string, NodeText>();
		private Dictionary<string, NodePos> _posCache = new Dictionary<string, NodePos>();
		private Dictionary<string, NodeTel> _telCache = new Dictionary<string, NodeTel>();

		private readonly TimeSpan _infoFresh = TimeSpan.FromMinutes(30);
		private readonly TimeSpan _textFresh = TimeSpan.FromMinutes(30);
		private readonly TimeSpan _posFresh = TimeSpan.FromMinutes(30);
		private readonly TimeSpan _telFresh = TimeSpan.FromMinutes(30);

        public MeshMqttBridge(
            APRSGateWay gw,
            string host, int port,
            bool useTls, bool tlsIgnoreErrors,
            string user, string pass,
            string topic, int keepAlive,
            string nodePrefix, string symbol, string commentSuffix, int threadSleep)
        {
            _gw = gw;
			_host = IsNullOrWhiteSpace(host) ? "mqtt.meshtastic.org" : host;
			_port = port > 0 ? port : (useTls ? 8883 : 1883);
			_useTls = useTls;
			_tlsIgnoreErrors = tlsIgnoreErrors;
			_user = (user == null) ? "meshdev" : user;
			_pass = (pass == null) ? "large4cats" : pass;
			_topic = IsNullOrWhiteSpace(topic) ? "msh/US/#" : topic;
			_keepAlive = (keepAlive > 0) ? keepAlive : 60;
			_nodePrefix = IsNullOrWhiteSpace(nodePrefix) ? "MT0XYZ" : nodePrefix;
			_symbol = IsNullOrWhiteSpace(symbol) ? "/[" : symbol;
			_commentSuffix = IsNullOrWhiteSpace(commentSuffix) ? "via Meshtastic" : commentSuffix;
			_threadSleep = (threadSleep > 0) ? threadSleep : 3000;
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

		private static bool IsTcpAlive(TcpClient tcp, Stream s)
		{
			if (tcp == null || s == null) return false;
			try
			{
				if (!tcp.Connected) return false;
				var soc = tcp.Client;
				bool part1 = soc.Poll(0, SelectMode.SelectRead);
				bool part2 = (soc.Available == 0);
				if (part1 && part2) return false;
				return true;
			}
			catch { return false; }
		}

		private void Run()
		{
			TcpClient tcp = null;
			Stream netStream = null;
			int backoffMs = 2000;

			while (_running)
			{
				try
				{
					if (!IsTcpAlive(tcp, netStream))
					{
						try { netStream.Dispose(); } catch { }
						try { tcp.Close(); } catch { }
						netStream = null; tcp = null;

						tcp = new TcpClient();
						tcp.NoDelay = true;
						tcp.Connect(_host, _port);
						try { tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch { }

						netStream = tcp.GetStream();
						if (_useTls)
						{
							var ssl = new SslStream(netStream, false,
								(sender, cert, chain, errs) => _tlsIgnoreErrors || errs == SslPolicyErrors.None);
							ssl.AuthenticateAsClient(_host);
							netStream = ssl;
						}

						try { netStream.ReadTimeout = 15000; netStream.WriteTimeout = 15000; } catch { }

						string clientId = "APRSGW-" + Guid.NewGuid().ToString("N").Substring(0, 10);
						SendConnect(netStream, clientId, _keepAlive, _user, _pass);

						Packet pkt;
						DateTime deadline = DateTime.UtcNow.AddSeconds(10);
						for (;;)
						{
							pkt = ReadPacket(netStream);
							if (pkt.Type == 0x20) break;
							if (DateTime.UtcNow > deadline) throw new Exception("Timeout waiting for CONNACK");
						}
						if (pkt.Payload.Length < 2 || pkt.Payload[1] != 0x00)
							throw new Exception("MQTT CONNACK failed");

						Console.WriteLine("[MQTT] Connected to " + _host + ":" + _port + " (TLS=" + _useTls + ")");

						SendSubscribe(netStream, 1, _topic, 0);
						deadline = DateTime.UtcNow.AddSeconds(10);
						for (;;)
						{
							pkt = ReadPacket(netStream);
							if (pkt.Type == 0x90) break;
							if (DateTime.UtcNow > deadline) throw new Exception("Timeout waiting for SUBACK");
						}
						Console.WriteLine("[MQTT] Subscribed to '" + _topic + "'");

						backoffMs = 5000;
					}

					DateTime lastTx = DateTime.UtcNow;

					while (_running && IsTcpAlive(tcp, netStream))
					{
						if ((DateTime.UtcNow - lastTx).TotalSeconds > Math.Max(10, _keepAlive / 2))
						{
							try { SendPingReq(netStream); lastTx = DateTime.UtcNow; }
							catch (IOException) { break; }
							catch { continue; }
						}

						if (!netStream.CanRead) { Thread.Sleep(100); continue; }

						try { netStream.ReadTimeout = 1000; } catch { }

						Packet p;
						try
						{
							p = ReadPacket(netStream);
						}
						catch (IOException)
						{
							continue;
						}
						catch (ObjectDisposedException)
						{
							break;
						}
						catch (Exception)
						{
							continue;
						}

						if (p.Type == 0 || p.Payload == null || p.Payload.Length == 0) continue;

						if (p.Type == 0x30 || (p.Type & 0xF0) == 0x30)
						{
							if (p.Payload.Length < 2) continue;
							int tlen = (p.Payload[0] << 8) | p.Payload[1];
							if (2 + tlen > p.Payload.Length) continue;

							string topic = Encoding.UTF8.GetString(p.Payload, 2, tlen);
							int skip = 2 + tlen;
							if (skip < 0 || skip > p.Payload.Length) continue;

							string msg = Encoding.UTF8.GetString(p.Payload, skip, p.Payload.Length - skip);
							try { HandlePublish(msg, topic); }
							catch { }
						}
						else if (p.Type == 0xD0)
						{
						}
						else
						{
							continue;
						}
					}
				}
				catch (Exception ex)
				{
					bool dead = !IsTcpAlive(tcp, netStream);
					Console.WriteLine(dead ? "[MQTT] Disconnected (" + ex.GetType().Name + "). Will reconnect." : "[MQTT] Non-fatal error: " + ex.GetType().Name + ". Staying on same socket.");
					if (!dead) continue;

					try { netStream.Dispose(); } catch { }
					try { tcp.Close(); } catch { }
					netStream = null; tcp = null;

					Thread.Sleep(backoffMs);
					backoffMs = Math.Min(backoffMs * 2, 30000);
				}
				catch
				{
				}

				if (_running)
				{
					Thread.Sleep(backoffMs);
					backoffMs = Math.Min(backoffMs * 2, 30000);
				}
			}

			try { netStream.Dispose(); } catch { }
			try { tcp.Close(); } catch { }
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

		private static string ExtractAnyLongName(string json)
		{
			if (string.IsNullOrEmpty(json)) return null;
			string s;
			s = ExtractFirstString(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"longname\"\\s*:\\s*\"([^\"]+)\""); if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"longName\"\\s*:\\s*\"([^\"]+)\""); if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"longName\"\\s*:\\s*\"([^\"]+)\"");   if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"longname\"\\s*:\\s*\"([^\"]+)\"");                              if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"longName\"\\s*:\\s*\"([^\"]+)\"");                              if (!string.IsNullOrEmpty(s)) return s;
			return null;
		}
		private static string ExtractAnyShortName(string json)
		{
			if (string.IsNullOrEmpty(json)) return null;
			string s;
			s = ExtractFirstString(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"shortname\"\\s*:\\s*\"([^\"]+)\""); if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"shortName\"\\s*:\\s*\"([^\"]+)\""); if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"shortName\"\\s*:\\s*\"([^\"]+)\"");    if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"shortname\"\\s*:\\s*\"([^\"]+)\"");                              if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"shortName\"\\s*:\\s*\"([^\"]+)\"");                              if (!string.IsNullOrEmpty(s)) return s;
			return null;
		}
		private static string ExtractStableId(string json)
		{
			if (string.IsNullOrEmpty(json)) return null;
			string s;
			s = ExtractFirstString(json, "\"sender\"\\s*:\\s*\"([^\"]+)\"");                                if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"id\"\\s*:\\s*\"([^\"]+)\"");       if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"user\"\\s*:\\s*\\{[^}]*?\"id\"\\s*:\\s*\"([^\"]+)\"");          if (!string.IsNullOrEmpty(s)) return s;
			s = ExtractFirstString(json, "\"id\"\\s*:\\s*\"([^\"]+)\"");                                    if (!string.IsNullOrEmpty(s)) return s;
			var m = System.Text.RegularExpressions.Regex.Match(json, "\"from\"\\s*:\\s*(\\d+)");
			ulong n;
			if (m.Success)
			{
				if (ulong.TryParse(m.Groups[1].Value, out n))
					return "!" + n.ToString("x8");
			}
			return null;
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

			m = Regex.Match(json, "\"(environmentMetrics\"\\s*:\\s*\\{[^}]*?)?\"temperature(C)?\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (!m.Success)
				m = Regex.Match(json, "\"temperature\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
			if (m.Success)
			{
				string v = (m.Groups.Count >= 4 && m.Groups[3].Success) ? m.Groups[3].Value : m.Groups[m.Groups.Count - 1].Value;
				tempC = ToNumStr1(v) + "C";
			}

			m = Regex.Match(json, "\"(relative)?hum(idity)?\"\\s*:\\s*(\\d+(?:\\.\\d+)?)", RegexOptions.CultureInvariant);
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

		private static bool TryExtractNodeInfoFromJson(string json, out string name, out string fw, out string hw, out string model)
		{
			name = fw = hw = model = null;
			if (string.IsNullOrEmpty(json)) return false;

			string s = ExtractFirstString(json, "\"longName\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(s)) s = ExtractFirstString(json, "\"shortName\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(s)) s = ExtractFirstString(json, "\"name\"\\s*:\\s*\"([^\"]+)\"");
			name = s;

			fw = ExtractFirstString(json, "\"firmware\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(fw)) fw = ExtractFirstString(json, "\"fw\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(fw)) fw = ExtractFirstString(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");

			hw = ExtractFirstString(json, "\"hardware\"\\s*:\\s*\"([^\"]+)\"");
			if (string.IsNullOrEmpty(hw)) hw = ExtractFirstString(json, "\"hw(Model|Version)?\"\\s*:\\s*\"([^\"]+)\"");
			model = ExtractFirstString(json, "\"model\"\\s*:\\s*\"([^\"]+)\"");

			return (name != null) || (fw != null) || (hw != null) || (model != null);
		}

		private static string MakeInfoSnippet(string name, string fw, string hw, string model, string chan)
		{
			StringBuilder sb = new StringBuilder();
			if (!IsNullOrWhiteSpace(name))  { sb.Append("Node:").Append(SanitizeAscii(name, 18)).Append(' '); }
			if (!IsNullOrWhiteSpace(fw))    { sb.Append("FW:").Append(SanitizeAscii(fw, 12)).Append(' '); }
			if (!IsNullOrWhiteSpace(hw))    { sb.Append("HW:").Append(SanitizeAscii(hw, 12)).Append(' '); }
			if (!IsNullOrWhiteSpace(model)) { sb.Append("MD:").Append(SanitizeAscii(model, 10)).Append(' '); }
			if (!IsNullOrWhiteSpace(chan))  { sb.Append("Ch:").Append(SanitizeAscii(chan, 10)).Append(' '); }
			string outp = sb.ToString().Trim();
			if (outp.Length > 70) outp = outp.Substring(0, 70);
			return outp;
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

			Match mt =
				Regex.Match(json, "\"payload\"\\s*:\\s*\\{[^}]*?\"text\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);
			if (!mt.Success)
				mt = Regex.Match(json, "\"decoded\"\\s*:\\s*\\{[^}]*?\"(payload|text)\"[^}]*?\"text\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);
			if (!mt.Success)
				mt = Regex.Match(json, "\"text\"\\s*:\\s*\"((?:\\\\.|[^\"])*)\"", RegexOptions.CultureInvariant);

			if (!mt.Success) return false;

			string raw = (mt.Groups.Count >= 3 && mt.Groups[2].Success) ? mt.Groups[2].Value : mt.Groups[1].Value;
			text = raw.Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ");
			text = SanitizeAscii(text, 67);
			if (text.Length == 0) return false;

			string[] keys = new string[] { "toCall", "toId", "destination", "destinationId", "dest", "to" };
			for (int i = 0; i < keys.Length; i++)
			{
				string pat = "\"" + keys[i] + "\"\\s*:\\s*\"([^\"]+)\"";
				Match md = Regex.Match(json, pat, RegexOptions.CultureInvariant);
				if (md.Success)
				{
					/* string cand = ToLegalAprsCallsign(md.Groups[1].Value);
					if (Regex.IsMatch(cand, "^[A-Z0-9]{1,6}(-([0-9]|1[0-5]))?$"))
						to = cand; */
					break;
				}
			}

			return true;
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

		private readonly Dictionary<string,string> _idToName = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
		private readonly Dictionary<string, NodePos> _posById = new Dictionary<string, NodePos>(StringComparer.OrdinalIgnoreCase);

		private void HandlePublish(string msg, string topic)
		{
			try
			{
				if (string.IsNullOrEmpty(msg)) return;

				string callsign = ExtractSenderFromJson(msg);
				string stableId = ExtractStableId(msg);
				string longName  = ExtractAnyLongName(msg);
				string shortName = ExtractAnyShortName(msg);

				string pretty = null;
				if (!IsNullOrWhiteSpace(longName))       pretty = longName; // ToLegalAprsCallsign(longName);
				else if (!IsNullOrWhiteSpace(shortName)) pretty = shortName; // ToLegalAprsCallsign(shortName);

				if (!IsNullOrWhiteSpace(pretty) && pretty.IndexOf('-') < 0 && !IsNullOrWhiteSpace(stableId))
					pretty = pretty + SafeSuffixFromJson(msg);

				if (!IsNullOrWhiteSpace(stableId) && !IsNullOrWhiteSpace(pretty))
					_idToName[stableId] = pretty;

				if (!IsNullOrWhiteSpace(pretty))
				{
					callsign = pretty;
				}
				else if (!IsNullOrWhiteSpace(stableId))
				{
					string mappedName;
					if (_idToName.TryGetValue(stableId, out mappedName) && !IsNullOrWhiteSpace(mappedName))
						callsign = mappedName;
					else if (IsNullOrWhiteSpace(callsign))
						callsign = stableId;
				}

				/* if (!IsNullOrWhiteSpace(callsign))
					callsign = ToLegalAprsCallsign(callsign); */

				string nName, nFw, nHw, nModel;
				if (TryExtractNodeInfoFromJson(msg, out nName, out nFw, out nHw, out nModel))
				{
					string ch = ChannelFromTopic(topic);
					NodeInfo ni;
					if (!_infoCache.TryGetValue(callsign, out ni)) ni = new NodeInfo();
					if (!IsNullOrWhiteSpace(nName))  ni.Name  = nName;
					if (!IsNullOrWhiteSpace(nFw))    ni.Fw    = nFw;
					if (!IsNullOrWhiteSpace(nHw))    ni.Hw    = nHw;
					if (!IsNullOrWhiteSpace(nModel)) ni.Model = nModel;
					if (!IsNullOrWhiteSpace(ch))     ni.Chan  = ch;
					ni.Ts = DateTime.UtcNow;
					_infoCache[callsign] = ni;
				}

				string tmsg, dest;
				if (TryExtractTextFromJson(msg, out tmsg, out dest))
				{
					NodeText nt;
					if (!_textCache.TryGetValue(callsign, out nt)) nt = new NodeText();
					nt.Text = tmsg;
					nt.To   = dest;
					nt.Ts   = DateTime.UtcNow;
					_textCache[callsign] = nt;
				}

				string battPct, volt, tempC, humPct, pressHPa, rssi, snr, airUtil;
				bool hasTel = TryExtractTelemetryFromJson(msg, out battPct, out volt, out tempC, out humPct, out pressHPa, out rssi, out snr, out airUtil);

				StringBuilder tel = new StringBuilder();
				if (hasTel)
				{
					if (!IsNullOrWhiteSpace(battPct)) tel.Append("Batt ").Append(battPct).Append(" ");
					if (!IsNullOrWhiteSpace(volt))    tel.Append("V").Append(volt).Append(" ");
					if (!IsNullOrWhiteSpace(tempC))   tel.Append("T").Append(tempC).Append(" ");
					if (!IsNullOrWhiteSpace(humPct))  tel.Append("H").Append(humPct).Append(" ");
					if (!IsNullOrWhiteSpace(pressHPa))tel.Append("P").Append(pressHPa).Append(" ");
					if (!IsNullOrWhiteSpace(snr))     tel.Append("SNR").Append(snr).Append(" ");
					if (!IsNullOrWhiteSpace(rssi))    tel.Append("RSSI").Append(rssi).Append(" ");
					if (!IsNullOrWhiteSpace(airUtil)) tel.Append("Air").Append(airUtil).Append(" ");
				}
				string telSnippet = tel.ToString().Trim();

				if (!IsNullOrWhiteSpace(telSnippet))
				{
					NodeTel t;
					if (!_telCache.TryGetValue(callsign, out t)) t = new NodeTel();
					t.Snippet = telSnippet;
					t.Ts = DateTime.UtcNow;
					_telCache[callsign] = t;
				}

				double lat = 0.0, lon = 0.0;
				int? alt = null;
				bool gotPosNow = TryExtractPositionFromJson(msg, out lat, out lon, out alt);

				if (gotPosNow) {
					NodePos p;
					if (!_posCache.TryGetValue(callsign, out p)) p = new NodePos();
					p.Lat = lat; p.Lon = lon; p.Alt = alt; p.Ts = DateTime.UtcNow;
					_posCache[callsign] = p;

					if (!IsNullOrWhiteSpace(stableId))
						_posById[stableId] = p;
				}

				string telToUse = telSnippet;
				if (IsNullOrWhiteSpace(telToUse))
				{
					NodeTel cachedTel;
					if (_telCache.TryGetValue(callsign, out cachedTel) &&
						(DateTime.UtcNow - cachedTel.Ts) <= _telFresh)
					{
						telToUse = cachedTel.Snippet;
					}
				}

				string posComment = "";
				if (alt.HasValue) posComment = BuildComment(null, alt, "");

				if (!IsNullOrWhiteSpace(telToUse))
				{
					if (!IsNullOrWhiteSpace(posComment)) posComment += " ";
					posComment += telToUse;
				}

				NodeInfo cachedInfo;
				if (_infoCache.TryGetValue(callsign, out cachedInfo) &&
					(DateTime.UtcNow - cachedInfo.Ts) <= _infoFresh)
				{
					StringBuilder brief = new StringBuilder();
					if (!IsNullOrWhiteSpace(cachedInfo.Name)) brief.Append("Node:").Append(SanitizeAscii(cachedInfo.Name, 18)).Append(' ');
					if (!IsNullOrWhiteSpace(cachedInfo.Chan)) brief.Append("Ch:").Append(SanitizeAscii(cachedInfo.Chan, 10)).Append(' ');
					string briefStr = brief.ToString().Trim();
					if (!IsNullOrWhiteSpace(briefStr))
					{
						if (!IsNullOrWhiteSpace(posComment)) posComment += " ";
						posComment += briefStr;
					}
				}

				NodeText cachedText;
				if (_textCache.TryGetValue(callsign, out cachedText) &&
					(DateTime.UtcNow - cachedText.Ts) <= _textFresh &&
					!IsNullOrWhiteSpace(cachedText.Text))
				{
					string tShort = SanitizeAscii(cachedText.Text, 30);
					if (!IsNullOrWhiteSpace(tShort))
					{
						if (!IsNullOrWhiteSpace(posComment)) posComment += " ";
						posComment += "Msg " + tShort;
					}
				}

				if (posComment.Length > 70) posComment = posComment.Substring(0, 70);

				bool sendPos = gotPosNow;
				if (!sendPos) {
					NodePos posCached;
					if (_posCache.TryGetValue(callsign, out posCached) && (DateTime.UtcNow - posCached.Ts) <= _posFresh) {
						lat = posCached.Lat; lon = posCached.Lon; alt = posCached.Alt;
						sendPos = true;
					} else if (!IsNullOrWhiteSpace(stableId)) {
						if (_posById.TryGetValue(stableId, out posCached) && (DateTime.UtcNow - posCached.Ts) <= _posFresh) {
							lat = posCached.Lat; lon = posCached.Lon; alt = posCached.Alt;
							sendPos = true;
						}
					}
				}

				if (sendPos)
				{
					string commentOut = (posComment + " " + _commentSuffix).Trim();
					string line = BuildAprsPositionLine(callsign, lat, lon, _symbol, commentOut);
					Console.WriteLine("[MQTT] DATA " + line);
					_gw.TCPSend("ignored", 0, line);
				}
				else
				{
					string statusBody = (posComment + " " + _commentSuffix).Trim();
					if (!IsNullOrWhiteSpace(statusBody))
					{
						string status = BuildAprsStatusLine(callsign, SanitizeAscii(statusBody, 67));
						Console.WriteLine("[MQTT] STATUS " + status);
					}
				}
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
