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

namespace APRSWebServer
{
    public class APRSData
    {
		private static bool IsNullOrNine(double lat, double lon)
		{
			const double eps = 1e-6;
			bool isZeroZero = Math.Abs(lat) < eps && Math.Abs(lon) < eps;
			bool isNineNine = Math.Abs(Math.Abs(lat) - 9.0) < eps && Math.Abs(Math.Abs(lon) - 9.0) < eps;
			return isZeroZero || isNineNine;
		}

        public static int CallsignChecksum(string callsign)
        {
            if (callsign == null) return 99999;
            if (callsign.Length == 0) return 99999;
            if (callsign.Length > 10) return 99999;

            int stophere = callsign.IndexOf("-");
            if (stophere > 0) callsign = callsign.Substring(0, stophere);
            string realcall = callsign.ToUpper();
            while (realcall.Length < 10) realcall += " ";

            int hash = 0x73e2;
            int i = 0;
            int len = realcall.Length;

            while (i < len)
            {
                hash ^= (int)(realcall.Substring(i, 1))[0] << 8;
                hash ^= (int)(realcall.Substring(i + 1, 1))[0];
                i += 2;
            }

            return hash & 0x7fff;
        }

        public static bool ParseAPRSRoute(string line, out string callsign, out string route, out string packet)
        {
            callsign = ""; route = ""; packet = "";
            if (line.IndexOf("#") == 0) return false;
            int fChr = line.IndexOf(">");
            if (fChr <= 1) return false;
            int sChr = line.IndexOf(":");
            if (sChr < fChr) return false;

            callsign = line.Substring(0, fChr);
            route = line.Substring(fChr + 1, sChr - fChr - 1);
            packet = line.Substring(sChr + 1);
            return true;
        }

        public static Buddie ParseAPRSPacket(string line)
        {
            if (line.IndexOf("#") == 0) return null;

            int fChr = line.IndexOf(">");
            if (fChr <= 1) return null;
            int sChr = line.IndexOf(":");
            if (sChr < fChr) return null;

            string callsign = line.Substring(0, fChr);
            string pckroute = line.Substring(fChr + 1, sChr - fChr - 1);
            string packet = line.Substring(sChr);

            if (packet.Length < 2) return null;

            Buddie b = new Buddie(callsign, 0, 0, 0, 0);
            b.APRS = line;

            switch (packet[1])
            {
                case ';':
                    int sk0 = Math.Max(packet.IndexOf("*", 2, 10), packet.IndexOf("_", 2, 10));
                    if (sk0 < 0) return null;
                    string obj_name = packet.Substring(2, sk0 - 2).Trim();
                    if (packet.IndexOf("*") > 0)
                        return ParseAPRSPacket(obj_name + ">" + pckroute + ":@" + packet.Substring(sk0 + 1));
                    break;

                case ')':
                    int sk1 = Math.Max(packet.IndexOf("!", 2, 10), packet.IndexOf("_", 2, 10));
                    if (sk1 < 0) return null;
                    string rep_name = packet.Substring(2, sk1 - 2).Trim();
                    if (packet.IndexOf("!") > 0)
                        return ParseAPRSPacket(rep_name + ">" + pckroute + ":@" + packet.Substring(sk1 + 1));
                    break;

                case '!':
                case '=':
                case '/':
                case '@':
                    {
                        string pos = packet.Substring(2);
                        if (pos[0] == '!') break;

                        DateTime received = DateTime.UtcNow;
                        if (pos[0] != '/')
                        {
                            switch (packet[8])
                            {
                                case 'z':
                                    received = new DateTime(DateTime.Now.Year, DateTime.Now.Month, int.Parse(packet.Substring(2, 2)),
                                    int.Parse(packet.Substring(4, 2)), int.Parse(packet.Substring(6, 2)), 0, DateTimeKind.Utc);
                                    pos = packet.Substring(9);
                                    break;
                                case '/':
                                    received = new DateTime(DateTime.Now.Year, DateTime.Now.Month, int.Parse(packet.Substring(2, 2)),
                                    int.Parse(packet.Substring(4, 2)), int.Parse(packet.Substring(6, 2)), 0, DateTimeKind.Local);
                                    pos = packet.Substring(9);
                                    break;
                                case 'h':
                                    received = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day,
                                    int.Parse(packet.Substring(2, 2)), int.Parse(packet.Substring(4, 2)), int.Parse(packet.Substring(6, 2)), DateTimeKind.Local);
                                    pos = packet.Substring(9);
                                    break;
                            };
                        };
                        b.last = received;

                        string aftertext = "";
                        char prim_or_sec = '/';
                        char symbol = '>';

                        if (pos[0] == '/')
                        {
                            string yyyy = pos.Substring(1, 4);
                            b.lat = 90 - (((byte)yyyy[0] - 33) * Math.Pow(91, 3) + ((byte)yyyy[1] - 33) * Math.Pow(91, 2) + ((byte)yyyy[2] - 33) * 91 + ((byte)yyyy[3] - 33)) / 380926;
                            string xxxx = pos.Substring(5, 4);
                            b.lon = -180 + (((byte)xxxx[0] - 33) * Math.Pow(91, 3) + ((byte)xxxx[1] - 33) * Math.Pow(91, 2) + ((byte)xxxx[2] - 33) * 91 + ((byte)xxxx[3] - 33)) / 190463;
                            symbol = pos[9];
                            string cmpv = pos.Substring(10, 2);
                            int addIfWeather = 0;
                            if (cmpv[0] == '_')
                            {
                                symbol = '_';
                                cmpv = pos.Substring(11, 2);
                                addIfWeather = 1;
                            };
                            if (cmpv[0] != ' ')
                            {
                                int cmpt = ((byte)pos[12 + addIfWeather] - 33);
                                if (((cmpt & 0x18) == 0x18) && (cmpv[0] != '{') && (cmpv[0] != '|'))
                                {
                                    b.course = (short)(((byte)cmpv[0] - 33) * 4);
                                    b.speed = (short)(((int)Math.Pow(1.08, ((byte)cmpv[1] - 33)) - 1) * 1.852);
                                };
                            };
                            aftertext = pos.Substring(13 + addIfWeather);
                            b.iconSymbol = "/" + symbol.ToString();
                        }
                        else
                        {
                            if (pos.Substring(0, 18).Contains(" ")) return null;

                            b.lat = double.Parse(pos.Substring(2, 5), System.Globalization.CultureInfo.InvariantCulture);
                            b.lat = double.Parse(pos.Substring(0, 2), System.Globalization.CultureInfo.InvariantCulture) + b.lat / 60;
                            if (pos[7] == 'S') b.lat *= -1;

                            b.lon = double.Parse(pos.Substring(12, 5), System.Globalization.CultureInfo.InvariantCulture);
                            b.lon = double.Parse(pos.Substring(9, 3), System.Globalization.CultureInfo.InvariantCulture) + b.lon / 60;
                            if (pos[17] == 'W') b.lon *= -1;

                            prim_or_sec = pos[8];
                            symbol = pos[18];
                            aftertext = pos.Substring(19);

                            b.iconSymbol = prim_or_sec.ToString() + symbol.ToString();
                        };

                        if ((symbol != '_') && (aftertext.Length >= 7) && (aftertext[3] == '/'))
                        {
                            short.TryParse(aftertext.Substring(0, 3), out b.course);
                            short.TryParse(aftertext.Substring(4, 3), out b.speed);
                            aftertext = aftertext.Remove(0, 7);
                        };

                        b.Comment = aftertext.Trim();

                    };
                    break;

                default:
                    break;
            };

			if (IsNullOrNine(b.lat, b.lon))
				return null;

            if (line.IndexOf(":>") > 0) b.Status = line.Substring(line.IndexOf(":>") + 2);
            return b;
        }

        public class Buddie
        {
            public static Regex BuddieNameRegex = new Regex("^([A-Z0-9]{3,9})$");
            public static Regex BuddieCallSignRegex = new Regex(@"^([A-Z0-9\-]{3,9})$");
            public static string symbolAny = "123456789ABCDEFGHJKLMNOPRSTUVWXYZ";
            public static int symbolAnyLength = 33;

            public static bool IsNullIcon(string symbol)
            {
                return (symbol == null) || (symbol == String.Empty) || (symbol == "//");
            }

            public bool Verified = false;
            public bool Owner = false;
            private string qConstruct { get {
                if (Verified && Owner) return ",qAC";
                if ((!Verified) && Owner) return ",qAX";
                if (Verified && (!Owner)) return ",qAO";
                if ((!Verified) && (!Owner)) return ",qAo";
                return "";
            } }
            
            public string name;
            public double lat;
            public double lon;
            public short speed;
            public short course;
            public uint alt;
            public string APRS = "";
            public string iconSymbol = "//";
            public string Status = "";
            public DateTime last;

            public byte[] APRSData { get { return String.IsNullOrEmpty(APRS) ? null : System.Text.Encoding.ASCII.GetBytes(APRS + "\r\n"); } set { APRS = (value == null) || (value.Length == 0) ? "" : System.Text.Encoding.ASCII.GetString(value).Replace("\r", "").Replace("\n", ""); } }

            private string _comment = "";
			public string Comment
            {
                get
                {
                    return _comment;
                }
                set
                {
                    _comment = value;
                    Regex rx = new Regex(@"/A=(?<ALT>\d+)", RegexOptions.IgnoreCase);
                    Match mx = rx.Match(_comment);
                    if (mx.Success) alt = uint.Parse(mx.Groups[1].Value);
                }
            }

			public bool PositionIsValid
			{
				get
				{
					const double eps = 1e-6;
					bool zeroZero = Math.Abs(lat) < eps && Math.Abs(lon) < eps;
					bool nineNine = Math.Abs(Math.Abs(lat) - 9.0) < eps && Math.Abs(Math.Abs(lon) - 9.0) < eps;
					return !(zeroZero || nineNine);
				}
			}

            public Buddie(string name, double lat, double lon, short speed, short course)
            {
                this.name = name.ToUpper();
                this.lat = lat;
                this.lon = lon;
                this.speed = speed;
                this.course = course;
                this.last = DateTime.UtcNow;
            }

            public void SetAPRSNoDate()
            {
				double alat = Math.Abs(lat);
				double alon = Math.Abs(lon);

				string latDeg = Math.Truncate(alat).ToString("00");
				string latMin = ((alat - Math.Truncate(alat)) * 60).ToString("00.00").Replace(",", ".");
				char latHem = lat >= 0 ? 'N' : 'S';

				string lonDeg = Math.Truncate(alon).ToString("000");
				string lonMin = ((alon - Math.Truncate(alon)) * 60).ToString("00.00").Replace(",", ".");
				char lonHem = lon >= 0 ? 'E' : 'W';

                APRS =
                    name + ">APRS,TCPIP*" + qConstruct + ":=" +
                    latDeg + latMin + latHem +
                    iconSymbol[0] +
                    lonDeg + lonMin + lonHem +
                    iconSymbol[1] +
                    course.ToString("000") + "/" + Math.Truncate(speed / 1.852).ToString("000") +
                    ((this.Comment != null) && (this.Comment != String.Empty) ? " " + this.Comment : "") +
                    "\r\n";
                APRSData = Encoding.ASCII.GetBytes(APRS);
            }

            public void SetAPRSWithDate()
            {
				double alat = Math.Abs(lat);
				double alon = Math.Abs(lon);

				string latDeg = Math.Truncate(alat).ToString("00");
				string latMin = ((alat - Math.Truncate(alat)) * 60).ToString("00.00").Replace(",", ".");
				char latHem = lat >= 0 ? 'N' : 'S';

				string lonDeg = Math.Truncate(alon).ToString("000");
				string lonMin = ((alon - Math.Truncate(alon)) * 60).ToString("00.00").Replace(",", ".");
				char lonHem = lon >= 0 ? 'E' : 'W';

                APRS =
                    name + ">APRS,TCPIP*" + qConstruct + ":@";
                if (DateTime.UtcNow.Subtract(this.last).TotalHours <= 23.5)
                    APRS += this.last.ToString("HHmmss") + "h";
                else
                    APRS += this.last.ToString("ddHHmm") + "z";
                APRS +=
                    latDeg + latMin + latHem +
                    iconSymbol[0] +
                    lonDeg + lonMin + lonHem +
                    iconSymbol[1] +
                    course.ToString("000") + "/" + Math.Truncate(speed / 1.852).ToString("000") +
                    ((this.Comment != null) && (this.Comment != String.Empty) ? " " + this.Comment : "") +
                    "\r\n";
                APRSData = Encoding.ASCII.GetBytes(APRS);
            }

            public string GetWebSocketText()
            {
                return String.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:HH:mm:ss dd.MM.yyyy} UTC {1} >> {2:000.0000000} {3:000.0000000} {4:00000.00} {5} {6} {7} {8}\r\n", new object[] { last, name, lat, lon, alt, course, speed, iconSymbol, _comment });
            }

            public override string ToString()
            {
                return String.Format("{0} >> {1} {2} {3}/{4} {5} {6}", new object[] { name, lat, lon, speed, course, iconSymbol, _comment });
            }

            public static int Hash(string name)
            {
                string upname = name == null ? "" : name;
                int stophere = upname.IndexOf("-");
                if (stophere > 0) upname = upname.Substring(0, stophere);
                while (upname.Length < 9) upname += " ";

                int hash = 0x2017;
                int i = 0;
                while (i < 9)
                {
                    hash ^= (int)(upname.Substring(i, 1))[0] << 16;
                    hash ^= (int)(upname.Substring(i + 1, 1))[0] << 8;
                    hash ^= (int)(upname.Substring(i + 2, 1))[0];
                    i += 3;
                };
                return hash & 0x7FFFFF;
            }

            public static uint MMSI(string name)
            {
                string upname = name == null ? "" : name;
                while (upname.Length < 9) upname += " ";
                int hash = 2017;
                int i = 0;
                while (i < 9)
                {
                    hash ^= (int)(upname.Substring(i, 1))[0] << 16;
                    hash ^= (int)(upname.Substring(i + 1, 1))[0] << 8;
                    hash ^= (int)(upname.Substring(i + 2, 1))[0];
                    i += 3;
                };
                return (uint)(hash & 0xFFFFFF);
            }

            public void FillFrom(Buddie b)
            {
                this.name = b.name;
                this.lat = b.lat;
                this.lon = b.lon;
                this.speed = b.speed;
                this.course = b.course;
                this.alt = b.alt;

                if (!String.IsNullOrEmpty(b.APRS)) this.APRS = b.APRS;
                if (!IsNullIcon(b.iconSymbol)) this.iconSymbol = b.iconSymbol;
                if (!String.IsNullOrEmpty(b._comment)) this._comment = b._comment;
                if (!String.IsNullOrEmpty(b.Status)) this.Status = b.Status;

                this.last = b.last;
            }
        }
    }
}
