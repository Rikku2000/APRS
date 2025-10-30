#if SQLITE
using System;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Collections.Generic;

namespace APRSWebServer
{
    public class APRSStorage : IDisposable
    {
        private readonly string _path;
        private readonly SQLiteConnection _conn;

        private readonly Queue<Action<SQLiteConnection>> _queue = new Queue<Action<SQLiteConnection>>();
        private readonly object _qLock = new object();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);

        private readonly Thread _writer;
        private volatile bool _run = true;

        public APRSStorage(string dbPath)
        {
            if (string.IsNullOrEmpty(dbPath)) dbPath = "aprs.sqlite";
            _path = dbPath;

            var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            _conn = new SQLiteConnection(string.Format("Data Source={0};Version=3;Cache=Shared", _path));
            _conn.Open();

            using (var cmd = _conn.CreateCommand())
            {
                cmd.CommandText =
                    "PRAGMA foreign_keys=ON; " +
                    "PRAGMA journal_mode=WAL; " +
                    "PRAGMA synchronous=NORMAL; " +

                    "CREATE TABLE IF NOT EXISTS packets (" +
                    "  id INTEGER PRIMARY KEY, " +
                    "  recv_utc TEXT NOT NULL, " +
                    "  callsign TEXT, " +
                    "  route TEXT, " +
                    "  payload TEXT, " +
                    "  raw TEXT NOT NULL, " +
                    "  client_ip TEXT, " +
                    "  validated INTEGER NOT NULL, " +
                    "  owner INTEGER NOT NULL" +
                    "); " +
                    "CREATE INDEX IF NOT EXISTS idx_packets_time ON packets(recv_utc); " +
                    "CREATE INDEX IF NOT EXISTS idx_packets_call ON packets(callsign); " +

                    "CREATE TABLE IF NOT EXISTS positions (" +
                    "  id INTEGER PRIMARY KEY, " +
                    "  packet_id INTEGER NOT NULL REFERENCES packets(id) ON DELETE CASCADE, " +
                    "  recv_utc TEXT NOT NULL, " +
                    "  callsign TEXT NOT NULL, " +
                    "  lat REAL NOT NULL, " +
                    "  lon REAL NOT NULL, " +
                    "  course INTEGER, " +
                    "  speed INTEGER, " +
                    "  symbol TEXT, " +
                    "  validated INTEGER NOT NULL" +
                    "); " +
                    "CREATE INDEX IF NOT EXISTS idx_positions_call_time ON positions(callsign, recv_utc DESC); " +
                    "CREATE INDEX IF NOT EXISTS idx_positions_time ON positions(recv_utc);";
                cmd.ExecuteNonQuery();
            }

            _writer = new Thread(Writer);
            _writer.IsBackground = true;
            _writer.Name = "AprsSqlWriter";
            _writer.Start();
        }

        public void SavePacket(string raw, APRSData.Buddie bud, string clientIp, bool validated, bool owner)
        {
            string callsign = (bud != null) ? bud.name : null;
            EnqueuePacket(raw, callsign, null, null, clientIp, validated, owner, bud);
        }

        public void SavePacket(
            string raw, string callsign, string route, string payload,
            string clientIp, bool validated, bool owner, APRSData.Buddie budOrNull)
        {
            EnqueuePacket(raw, callsign, route, payload, clientIp, validated, owner, budOrNull);
        }

        public void EnqueuePacket(
            string raw,
            string callsign,
            string route,
            string payload,
            string clientIp,
            bool validated,
            bool owner,
            APRSData.Buddie budOrNull)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

            Action<SQLiteConnection> job = delegate (SQLiteConnection conn)
            {
                using (var tx = conn.BeginTransaction())
                {
                    long packetId;

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText =
                            "INSERT INTO packets(recv_utc, callsign, route, payload, raw, client_ip, validated, owner) " +
                            "VALUES (@t,@c,@r,@p,@raw,@ip,@v,@o); " +
                            "SELECT last_insert_rowid();";
                        cmd.Parameters.AddWithValue("@t", now);
                        cmd.Parameters.AddWithValue("@c", (object)callsign ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@r", (object)route ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@p", (object)payload ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@raw", raw);
                        cmd.Parameters.AddWithValue("@ip", (object)clientIp ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@v", validated ? 1 : 0);
                        cmd.Parameters.AddWithValue("@o", owner ? 1 : 0);

                        object scalar = cmd.ExecuteScalar();
                        packetId = Convert.ToInt64(scalar);
                    }

                    if (budOrNull != null && budOrNull.PositionIsValid)
                    {
                        using (var cmd2 = conn.CreateCommand())
                        {
                            cmd2.CommandText =
                                "INSERT INTO positions(packet_id, recv_utc, callsign, lat, lon, course, speed, symbol, validated) " +
                                "VALUES (@pid,@t,@c,@lat,@lon,@crs,@spd,@sym,@v);";
                            cmd2.Parameters.AddWithValue("@pid", packetId);
                            cmd2.Parameters.AddWithValue("@t", now);
                            cmd2.Parameters.AddWithValue("@c", (object)(budOrNull.name ?? callsign ?? string.Empty));
                            cmd2.Parameters.AddWithValue("@lat", budOrNull.lat);
                            cmd2.Parameters.AddWithValue("@lon", budOrNull.lon);

                            cmd2.Parameters.AddWithValue("@crs", (budOrNull.course != 0) ? (object)budOrNull.course : DBNull.Value);
                            cmd2.Parameters.AddWithValue("@spd", (budOrNull.speed  != 0) ? (object)budOrNull.speed  : DBNull.Value);

                            cmd2.Parameters.AddWithValue("@sym", (object)(budOrNull.iconSymbol ?? string.Empty));
                            cmd2.Parameters.AddWithValue("@v", validated ? 1 : 0);
                            cmd2.ExecuteNonQuery();
                        }
                    }

                    tx.Commit();
                }
            };

            lock (_qLock)
            {
                _queue.Enqueue(job);
            }
            _wake.Set();
        }

        private void Writer()
        {
            while (_run)
            {
                Action<SQLiteConnection> job = null;

                lock (_qLock)
                {
                    if (_queue.Count > 0)
                        job = _queue.Dequeue();
                }

                if (job != null)
                {
                    try
                    {
                        job(_conn);

                        int n = 0;
                        while (n < 200)
                        {
                            Action<SQLiteConnection> more = null;
                            lock (_qLock)
                            {
                                if (_queue.Count > 0) more = _queue.Dequeue();
                            }
                            if (more == null) break;
                            try { more(_conn); } catch { }
                            n++;
                        }
                    }
                    catch { }

                    continue;
                }

                _wake.WaitOne(50);
            }
        }

        public void Dispose()
        {
            _run = false;
            try { _wake.Set(); } catch { }
            try { _writer.Join(500); } catch { }
            try { _conn.Dispose(); } catch { }
            try { _wake.Close(); } catch { } // .NET 4.0 safe
        }
    }
}
#endif
