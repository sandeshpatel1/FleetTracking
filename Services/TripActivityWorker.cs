using Microsoft.Data.SqlClient;
using TrackingMVC.Data;

namespace TrackingMVC.Services
{
    /// <summary>
    /// Every PollSeconds, walks each OPEN trip (device assigned, not closed),
    /// reads the pings it hasn't processed yet (gps_locations_vta.trip_id =
    /// trip, id &gt; last processed) and writes activity events to
    /// gps_trip_events: HALT_START / HALT_END, LOW_BATTERY, DEVICE_OFFLINE /
    /// DEVICE_ONLINE, GEOFENCE_IN / GEOFENCE_OUT.
    ///
    /// Progress and open-halt state live in gps_trip_state, so a restart
    /// resumes where it left off. Each trip is processed in one transaction
    /// (events + state together), so a failure never leaves duplicate events.
    ///
    /// Lifecycle events (TRIP_CREATED / DEVICE_ASSIGNED / TRIP_ENDED) are NOT
    /// written here — the trg_trip_lifecycle trigger handles those.
    ///
    /// Settings (appsettings.json → "TripActivity"), all optional:
    ///   PollSeconds 60 · HaltMinutes 5 · StopSpeedKmh 3 · LowBatteryPct 50 · OfflineMinutes 30
    /// </summary>
    public class TripActivityWorker : BackgroundService
    {
        private readonly DbHelper _db;
        private readonly ILogger<TripActivityWorker> _log;
        private readonly int _pollSeconds;
        private readonly int _haltMinutes;
        private readonly int _lowBattery;
        private readonly int _offlineMinutes;
        private readonly double _stopSpeedKmh;

        public TripActivityWorker(DbHelper db, IConfiguration cfg, ILogger<TripActivityWorker> log)
        {
            _db = db;
            _log = log;
            var s = cfg.GetSection("TripActivity");
            _pollSeconds    = s.GetValue("PollSeconds", 60);
            _haltMinutes    = s.GetValue("HaltMinutes", 5);
            _stopSpeedKmh   = s.GetValue("StopSpeedKmh", 3.0);
            _lowBattery     = s.GetValue("LowBatteryPct", 50);
            _offlineMinutes = s.GetValue("OfflineMinutes", 30);
        }

        private sealed record OpenTrip(string TripId, string Imei);
        private sealed record Zone(int Id, string Name, double Lat, double Lng, double RadiusM);
        private sealed record Ping(long Id, double Lat, double Lng, double Speed, int Battery, DateTime At);

        private sealed class TripState
        {
            public long LastPingId;
            public DateTime? LastPingAt;
            public bool InHalt;
            public DateTime? HaltStart;
            public double? HaltLat, HaltLng;
            public bool LowBatFlag;
            public bool OfflineFlag;
            public int? ZoneId;
        }

        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { return; }

            while (!ct.IsCancellationRequested)
            {
                try { await RunCycleAsync(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Trip activity cycle failed");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(_pollSeconds), ct); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task RunCycleAsync(CancellationToken ct)
        {
            await using var con = _db.GetConnection();
            await con.OpenAsync(ct);

            var zones = await LoadZonesAsync(con, ct);
            var trips = await LoadOpenTripsAsync(con, ct);

            foreach (var trip in trips)
            {
                try { await ProcessTripAsync(con, trip, zones, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _log.LogError(ex, "Trip activity failed for trip {Trip}", trip.TripId);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        private async Task ProcessTripAsync(SqlConnection con, OpenTrip trip, List<Zone> zones, CancellationToken ct)
        {
            await using var txn = (SqlTransaction)await con.BeginTransactionAsync(ct);
            try
            {
                var st = await LoadStateAsync(con, txn, trip.TripId, ct);
                var pings = await LoadPingsAsync(con, txn, trip.TripId, st.LastPingId, ct);
                var dirty = pings.Count > 0;

                Task Ev(string type, DateTime at, double? lat, double? lng, string? details) =>
                    InsertEventAsync(con, txn, trip, type, at, lat, lng, details, ct);

                foreach (var p in pings)
                {
                    // ── Offline gap (device silent, then a ping arrives) ──
                    var gap = st.LastPingAt.HasValue && (p.At - st.LastPingAt.Value).TotalMinutes > _offlineMinutes;
                    if (gap && !st.OfflineFlag)
                        await Ev("DEVICE_OFFLINE", st.LastPingAt!.Value, null, null,
                                 $"no data for {(p.At - st.LastPingAt.Value).TotalMinutes:F0} min");
                    if (gap || st.OfflineFlag)
                    {
                        await Ev("DEVICE_ONLINE", p.At, p.Lat, p.Lng, null);
                        st.OfflineFlag = false;
                    }

                    // Invalid fix: nothing else to evaluate, but keep progress moving
                    if (p.Lat == 0 && p.Lng == 0)
                    {
                        st.LastPingId = p.Id; st.LastPingAt = p.At;
                        continue;
                    }

                    // ── Low battery (re-arms once it recovers 10 pts above the limit) ──
                    if (p.Battery > 0)
                    {
                        if (p.Battery < _lowBattery && !st.LowBatFlag)
                        {
                            await Ev("LOW_BATTERY", p.At, p.Lat, p.Lng, $"battery={p.Battery}%");
                            st.LowBatFlag = true;
                        }
                        else if (p.Battery >= _lowBattery + 10 && st.LowBatFlag)
                        {
                            st.LowBatFlag = false;
                        }
                    }

                    // ── Halts: stopped for >= HaltMinutes ──
                    if (p.Speed <= _stopSpeedKmh)
                    {
                        if (st.HaltStart == null) { st.HaltStart = p.At; st.HaltLat = p.Lat; st.HaltLng = p.Lng; }
                        if (!st.InHalt && (p.At - st.HaltStart.Value).TotalMinutes >= _haltMinutes)
                        {
                            await Ev("HALT_START", st.HaltStart.Value, st.HaltLat, st.HaltLng, null);
                            st.InHalt = true;
                        }
                    }
                    else
                    {
                        if (st.InHalt)
                        {
                            var mins = (p.At - st.HaltStart!.Value).TotalMinutes;
                            await Ev("HALT_END", p.At, p.Lat, p.Lng, $"duration={mins:F0} min");
                        }
                        st.InHalt = false; st.HaltStart = null; st.HaltLat = st.HaltLng = null;
                    }

                    // ── Geofence in / out (circle test, same rule as HomeController) ──
                    var zone = FindZone(p.Lat, p.Lng, zones);
                    if (zone?.Id != st.ZoneId)
                    {
                        if (st.ZoneId.HasValue)
                        {
                            var old = zones.FirstOrDefault(z => z.Id == st.ZoneId.Value);
                            await Ev("GEOFENCE_OUT", p.At, p.Lat, p.Lng, old?.Name ?? $"zone {st.ZoneId}");
                        }
                        if (zone != null)
                            await Ev("GEOFENCE_IN", p.At, p.Lat, p.Lng, zone.Name);
                        st.ZoneId = zone?.Id;
                    }

                    st.LastPingId = p.Id;
                    st.LastPingAt = p.At;
                }

                // ── Device gone quiet right now (no new ping to reveal the gap) ──
                if (st.LastPingAt.HasValue && !st.OfflineFlag)
                {
                    var silent = (DateTime.Now - st.LastPingAt.Value).TotalMinutes;
                    if (silent > _offlineMinutes)
                    {
                        await Ev("DEVICE_OFFLINE", DateTime.Now, null, null, $"no data for {silent:F0} min");
                        st.OfflineFlag = true;
                        dirty = true;
                    }
                }

                if (dirty) await SaveStateAsync(con, txn, trip.TripId, st, ct);
                await txn.CommitAsync(ct);
            }
            catch
            {
                await txn.RollbackAsync(CancellationToken.None);
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Data access
        // ════════════════════════════════════════════════════════════════
        private static async Task<List<OpenTrip>> LoadOpenTripsAsync(SqlConnection con, CancellationToken ct)
        {
            const string sql = @"
                SELECT [trip_ID], [device_id]
                FROM   [atmparking].[dbo].[gps_trip_detail]
                WHERE  ISNULL([assiged_flag], 0) = 1
                  AND  [close_Date] IS NULL
                  AND  [device_id] IS NOT NULL AND [device_id] <> ''";
            var list = new List<OpenTrip>();
            await using var cmd = new SqlCommand(sql, con);
            await using var dr = await cmd.ExecuteReaderAsync(ct);
            while (await dr.ReadAsync(ct))
                list.Add(new OpenTrip(dr["trip_ID"].ToString()!, dr["device_id"].ToString()!));
            return list;
        }

        private static async Task<List<Zone>> LoadZonesAsync(SqlConnection con, CancellationToken ct)
        {
            const string sql = @"SELECT [id],[latitude],[longitude],[name],[geofence]
                                 FROM [atmparking].[dbo].[gps_geo_fencing_location]";
            var list = new List<Zone>();
            await using var cmd = new SqlCommand(sql, con);
            await using var dr = await cmd.ExecuteReaderAsync(ct);
            while (await dr.ReadAsync(ct))
            {
                var lat = dr["latitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["latitude"]);
                var lng = dr["longitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["longitude"]);
                if (lat == 0 && lng == 0) continue;
                var radius = dr["geofence"] == DBNull.Value ? 0 : Convert.ToInt32(dr["geofence"]);
                list.Add(new Zone(Convert.ToInt32(dr["id"]), dr["name"].ToString() ?? "", lat, lng, Math.Max(radius, 30)));
            }
            return list;
        }

        private static async Task<TripState> LoadStateAsync(SqlConnection con, SqlTransaction txn, string tripId, CancellationToken ct)
        {
            const string sql = @"SELECT last_ping_id,last_ping_at,in_halt,halt_start,halt_lat,halt_lng,
                                        low_bat_flag,offline_flag,zone_id
                                 FROM [atmparking].[dbo].[gps_trip_state] WHERE trip_id = @t";
            await using var cmd = new SqlCommand(sql, con, txn);
            cmd.Parameters.AddWithValue("@t", tripId);
            await using var dr = await cmd.ExecuteReaderAsync(ct);
            if (!await dr.ReadAsync(ct)) return new TripState();

            return new TripState
            {
                LastPingId = Convert.ToInt64(dr["last_ping_id"]),
                LastPingAt = dr["last_ping_at"] == DBNull.Value ? null : Convert.ToDateTime(dr["last_ping_at"]),
                InHalt = Convert.ToBoolean(dr["in_halt"]),
                HaltStart = dr["halt_start"] == DBNull.Value ? null : Convert.ToDateTime(dr["halt_start"]),
                HaltLat = dr["halt_lat"] == DBNull.Value ? null : Convert.ToDouble(dr["halt_lat"]),
                HaltLng = dr["halt_lng"] == DBNull.Value ? null : Convert.ToDouble(dr["halt_lng"]),
                LowBatFlag = Convert.ToBoolean(dr["low_bat_flag"]),
                OfflineFlag = Convert.ToBoolean(dr["offline_flag"]),
                ZoneId = dr["zone_id"] == DBNull.Value ? null : Convert.ToInt32(dr["zone_id"])
            };
        }

        private static async Task SaveStateAsync(SqlConnection con, SqlTransaction txn, string tripId, TripState s, CancellationToken ct)
        {
            const string sql = @"
                MERGE [atmparking].[dbo].[gps_trip_state] AS t
                USING (SELECT @trip AS trip_id) AS src ON t.trip_id = src.trip_id
                WHEN MATCHED THEN UPDATE SET
                    last_ping_id=@lpid, last_ping_at=@lpat, in_halt=@inh, halt_start=@hs,
                    halt_lat=@hlat, halt_lng=@hlng, low_bat_flag=@lb, offline_flag=@off,
                    zone_id=@zone, updated_at=GETDATE()
                WHEN NOT MATCHED THEN INSERT
                    (trip_id,last_ping_id,last_ping_at,in_halt,halt_start,halt_lat,halt_lng,
                     low_bat_flag,offline_flag,zone_id,updated_at)
                    VALUES (@trip,@lpid,@lpat,@inh,@hs,@hlat,@hlng,@lb,@off,@zone,GETDATE());";
            await using var cmd = new SqlCommand(sql, con, txn);
            cmd.Parameters.AddWithValue("@trip", tripId);
            cmd.Parameters.AddWithValue("@lpid", s.LastPingId);
            cmd.Parameters.AddWithValue("@lpat", (object?)s.LastPingAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@inh", s.InHalt);
            cmd.Parameters.AddWithValue("@hs", (object?)s.HaltStart ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hlat", (object?)s.HaltLat ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@hlng", (object?)s.HaltLng ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lb", s.LowBatFlag);
            cmd.Parameters.AddWithValue("@off", s.OfflineFlag);
            cmd.Parameters.AddWithValue("@zone", (object?)s.ZoneId ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        private static async Task<List<Ping>> LoadPingsAsync(SqlConnection con, SqlTransaction txn, string tripId, long afterId, CancellationToken ct)
        {
            // Batches of 2000 per cycle: a long backlog is worked off over several cycles.
            const string sql = @"
                SELECT TOP 2000 [id],[latitude],[longitude],[speed],[battery],[created_at]
                FROM   [atmparking].[dbo].[gps_locations_vta]
                WHERE  [trip_id] = @t AND [id] > @after
                ORDER  BY [id] ASC";
            var list = new List<Ping>();
            await using var cmd = new SqlCommand(sql, con, txn);
            cmd.Parameters.AddWithValue("@t", tripId);
            cmd.Parameters.AddWithValue("@after", afterId);
            await using var dr = await cmd.ExecuteReaderAsync(ct);
            while (await dr.ReadAsync(ct))
            {
                if (dr["created_at"] == DBNull.Value) continue;
                list.Add(new Ping(
                    Convert.ToInt64(dr["id"]),
                    dr["latitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["latitude"]),
                    dr["longitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["longitude"]),
                    dr["speed"] == DBNull.Value ? 0 : Convert.ToDouble(dr["speed"]),
                    dr["battery"] == DBNull.Value ? 0 : Convert.ToInt32(dr["battery"]),
                    Convert.ToDateTime(dr["created_at"])));
            }
            return list;
        }

        private static async Task InsertEventAsync(SqlConnection con, SqlTransaction txn, OpenTrip trip,
            string type, DateTime at, double? lat, double? lng, string? details, CancellationToken ct)
        {
            const string sql = @"
                INSERT INTO [atmparking].[dbo].[gps_trip_events]
                       (trip_id, imei, event_type, event_time, latitude, longitude, details)
                VALUES (@t, @imei, @type, @at, @lat, @lng, @d)";
            await using var cmd = new SqlCommand(sql, con, txn);
            cmd.Parameters.AddWithValue("@t", trip.TripId);
            cmd.Parameters.AddWithValue("@imei", trip.Imei);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@at", at);
            cmd.Parameters.AddWithValue("@lat", (object?)lat ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lng", (object?)lng ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@d", (object?)details ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // ════════════════════════════════════════════════════════════════
        //  Geometry
        // ════════════════════════════════════════════════════════════════
        private static Zone? FindZone(double lat, double lng, List<Zone> zones)
        {
            Zone? best = null;
            var bestDist = double.MaxValue;
            foreach (var z in zones)
            {
                var d = HaversineM(lat, lng, z.Lat, z.Lng);
                if (d <= z.RadiusM && d < bestDist) { bestDist = d; best = z; }
            }
            return best;
        }

        private static double HaversineM(double lat1, double lng1, double lat2, double lng2)
        {
            const double R = 6371000;
            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLng = (lng2 - lng1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                     + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                     * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}
