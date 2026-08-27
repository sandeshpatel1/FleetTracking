using Microsoft.Data.SqlClient;
using TrackingMVC.Models.ViewModels;

namespace TrackingMVC.Data
{
    /// <summary>
    /// All DB access for Device Assignment — raw ADO.NET via DbHelper.
    ///
    /// Tables  (database: atmparking):
    ///
    ///   [dbo].[gps_trip_detail]
    ///     pk | trip_ID | vehicle_no | serial_no | assiged_flag | driver_no | device_id | battery
    ///
    ///   [dbo].[gps_devices_vta]
    ///     id | imei | last_ip | last_seen | AssignFlag
    ///
    ///   [dbo].[gps_locations_vta]
    ///     id | imei | latitude | longitude | speed | battery | protocol | raw_packet | created_at | trip_id
    /// </summary>
    public class DeviceAssignmentRepository
    {
        private readonly DbHelper _db;
        public DeviceAssignmentRepository(DbHelper db) => _db = db;

        // ════════════════════════════════════════════════════════════════════
        //  AUTOCOMPLETE
        //  searchType "trip"    → searches trip_ID    column
        //  searchType "vehicle" → searches vehicle_no column
        //  Returns up to 10 suggestions for the dropdown.
        // ════════════════════════════════════════════════════════════════════
        public async Task<List<AutocompleteItem>> GetSuggestionsAsync(
            string query, string searchType)
        {
            string sql = searchType == "trip"
                ? @"SELECT TOP 10
                        pk,
                        trip_ID    AS display,
                        vehicle_no,
                        trip_ID    AS tripCol,
                        ISNULL(assiged_flag, 0) AS assiged_flag
                    FROM [dbo].[gps_trip_detail]
                    WHERE trip_ID LIKE @q
                    ORDER BY pk DESC"
                : @"SELECT TOP 10
                        pk,
                        vehicle_no AS display,
                        vehicle_no,
                        trip_ID    AS tripCol,
                        ISNULL(assiged_flag, 0) AS assiged_flag
                    FROM [dbo].[gps_trip_detail]
                    WHERE vehicle_no LIKE @q
                    ORDER BY pk DESC";

            var list = new List<AutocompleteItem>();

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", query.Trim() + "%");

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add(new AutocompleteItem
                {
                    Pk = Convert.ToInt32(rdr["pk"]),
                    Display = rdr["display"]?.ToString() ?? "",
                    VehicleNo = rdr["vehicle_no"]?.ToString() ?? "",
                    TripId = rdr["tripCol"]?.ToString() ?? "",
                    AssignedFlag = rdr["assiged_flag"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["assiged_flag"])
                });
            }
            return list;
        }

        // ════════════════════════════════════════════════════════════════════
        //  TRIP DETAIL by PK
        //  Returns the full trip row including battery (test column).
        // ════════════════════════════════════════════════════════════════════
        public async Task<TripDetailDto?> GetTripByPkAsync(int pk)
        {
            const string sql =
                @"SELECT pk, trip_ID, vehicle_no, serial_no,
                         ISNULL(assiged_flag, 0) AS assiged_flag,
                         driver_no,
                         ISNULL(device_id, '') AS device_id,
                         ISNULL(battery, 0)    AS battery
                  FROM   [dbo].[gps_trip_detail]
                  WHERE  pk = @Pk";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Pk", pk);

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return null;
            return MapTrip(rdr);
        }

        // ════════════════════════════════════════════════════════════════════
        //  LAST GPS LOCATION by IMEI
        //  Reads latest row from gps_locations_vta for a given IMEI.
        //  Battery shown on the assigned-trip detail card comes from here.
        //  Returns null if the device has never sent a packet.
        // ════════════════════════════════════════════════════════════════════
        public async Task<LastLocationDto?> GetLastLocationByImeiAsync(string imei)
        {
            if (string.IsNullOrWhiteSpace(imei)) return null;

            const string sql =
                @"SELECT TOP 1
                      latitude, longitude, speed, battery, created_at
                  FROM  [dbo].[gps_locations_vta]
                  WHERE imei = @Imei
                  ORDER BY created_at DESC";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Imei", imei);

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return null;

            return new LastLocationDto
            {
                Latitude = rdr["latitude"] == DBNull.Value ? 0d : Convert.ToDouble(rdr["latitude"]),
                Longitude = rdr["longitude"] == DBNull.Value ? 0d : Convert.ToDouble(rdr["longitude"]),
                Speed = rdr["speed"] == DBNull.Value ? 0d : Convert.ToDouble(rdr["speed"]),
                Battery = rdr["battery"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["battery"]),
                RecordedAt = rdr["created_at"] == DBNull.Value
                                ? DateTime.MinValue
                                : Convert.ToDateTime(rdr["created_at"])
            };
        }

        // ════════════════════════════════════════════════════════════════════
        //  AVAILABLE UNASSIGNED DEVICES
        //
        //  Source:  gps_devices_vta   (AssignFlag = 0  →  not yet assigned)
        //  Battery: latest row in gps_locations_vta matched by imei
        //  last_seen comes from gps_devices_vta.last_seen directly
        //            (the tracker server updates this column on every packet)
        //
        //  The controller filters battery > 50 after this call.
        // ════════════════════════════════════════════════════════════════════
        public async Task<List<AvailableDeviceDto>> GetAvailableDevicesAsync()
        {
            const string sql =
                @"SELECT
                      d.id                            AS dev_id,
                      d.imei,
                      d.last_seen,
                      ISNULL(loc.battery, 0)          AS battery
                  FROM [dbo].[gps_devices_vta] d
                  LEFT JOIN [dbo].[gps_locations_vta] loc
                      ON loc.id = (
                          SELECT TOP 1 id
                          FROM   [dbo].[gps_locations_vta] v
                          WHERE  v.imei = d.imei
                          ORDER  BY v.created_at DESC
                      )
                  WHERE ISNULL(d.AssignFlag, 0) = 0
                  ORDER BY d.id";

            var list = new List<AvailableDeviceDto>();

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            await using var rdr = await cmd.ExecuteReaderAsync();

            while (await rdr.ReadAsync())
            {
                list.Add(new AvailableDeviceDto
                {
                    Pk = Convert.ToInt32(rdr["dev_id"]),
                    DeviceImei = rdr["imei"]?.ToString() ?? "",
                    BatteryLevel = rdr["battery"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["battery"]),
                    LastSeen = rdr["last_seen"] == DBNull.Value
                                    ? DateTime.Now.AddMinutes(-10)
                                    : Convert.ToDateTime(rdr["last_seen"])
                });
            }
            return list;
        }

        // ════════════════════════════════════════════════════════════════════
        //  CHECK IMEI — does it exist and is it already assigned?
        //  Used before the Assign call to give a friendly error early.
        // ════════════════════════════════════════════════════════════════════
        public async Task<(bool Exists, bool IsAssigned)> CheckImeiAsync(string imei)
        {
            const string sql =
                @"SELECT ISNULL(AssignFlag, 0) AS AssignFlag
                  FROM   [dbo].[gps_devices_vta]
                  WHERE  imei = @Imei";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Imei", imei);

            var result = await cmd.ExecuteScalarAsync();
            if (result == null) return (false, false);   // truly no matching row
            return (true, Convert.ToInt32(result) == 1);
        }

        // ════════════════════════════════════════════════════════════════════
        //  SEARCH DEVICES BY IMEI PREFIX (autocomplete on the scan/IMEI field)
        //
        //  Only returns unassigned devices (AssignFlag = 0) — no point
        //  suggesting a device that's already locked to another trip.
        //  Battery is read the same way as GetAvailableDevicesAsync.
        // ════════════════════════════════════════════════════════════════════
        public async Task<List<AvailableDeviceDto>> SearchImeiAsync(string query)
        {
            const string sql =
                @"SELECT TOP 10
                      d.id                            AS dev_id,
                      d.imei,
                      d.last_seen,
                      ISNULL(loc.battery, 0)          AS battery
                  FROM [dbo].[gps_devices_vta] d
                  LEFT JOIN [dbo].[gps_locations_vta] loc
                      ON loc.id = (
                          SELECT TOP 1 id
                          FROM   [dbo].[gps_locations_vta] v
                          WHERE  v.imei = d.imei
                          ORDER  BY v.created_at DESC
                      )
                  WHERE ISNULL(d.AssignFlag, 0) = 0
                    AND d.imei LIKE @q
                  ORDER BY d.id";

            var list = new List<AvailableDeviceDto>();

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@q", query.Trim() + "%");

            await using var rdr = await cmd.ExecuteReaderAsync();
            while (await rdr.ReadAsync())
            {
                list.Add(new AvailableDeviceDto
                {
                    Pk = Convert.ToInt32(rdr["dev_id"]),
                    DeviceImei = rdr["imei"]?.ToString() ?? "",
                    BatteryLevel = rdr["battery"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["battery"]),
                    LastSeen = rdr["last_seen"] == DBNull.Value
                                    ? DateTime.MinValue
                                    : Convert.ToDateTime(rdr["last_seen"])
                });
            }
            return list;
        }

        // ════════════════════════════════════════════════════════════════════
        //  GET SINGLE DEVICE BY IMEI (exact match — used by the scan/lookup step)
        //  Returns regardless of AssignFlag; the controller decides what to do
        //  with an already-assigned device via CheckImeiAsync.
        // ════════════════════════════════════════════════════════════════════
        public async Task<AvailableDeviceDto?> GetDeviceByImeiAsync(string imei)
        {
            const string sql =
                @"SELECT
                      d.id                            AS dev_id,
                      d.imei,
                      d.last_seen,
                      ISNULL(loc.battery, 0)          AS battery
                  FROM [dbo].[gps_devices_vta] d
                  LEFT JOIN [dbo].[gps_locations_vta] loc
                      ON loc.id = (
                          SELECT TOP 1 id
                          FROM   [dbo].[gps_locations_vta] v
                          WHERE  v.imei = d.imei
                          ORDER  BY v.created_at DESC
                      )
                  WHERE d.imei = @Imei";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Imei", imei);

            await using var rdr = await cmd.ExecuteReaderAsync();
            if (!await rdr.ReadAsync()) return null;

            return new AvailableDeviceDto
            {
                Pk = Convert.ToInt32(rdr["dev_id"]),
                DeviceImei = rdr["imei"]?.ToString() ?? "",
                BatteryLevel = rdr["battery"] == DBNull.Value ? 0 : Convert.ToInt32(rdr["battery"]),
                LastSeen = rdr["last_seen"] == DBNull.Value
                                ? DateTime.MinValue
                                : Convert.ToDateTime(rdr["last_seen"])
            };
        }

        // ════════════════════════════════════════════════════════════════════
        //  ASSIGN DEVICE TO TRIP — atomic transaction
        //
        //  Step 1: gps_devices_vta.AssignFlag   → 1   (lock the device)
        //  Step 2: gps_trip_detail.assiged_flag → 1   (lock the trip)
        //          gps_trip_detail.device_id   = @Imei
        //          gps_trip_detail.battery     = @Battery   (device battery at assign time)
        //          gps_trip_detail.assign_Date = GETDATE()
        //
        //  NOTE: gps_locations_vta.trip_id is intentionally NOT touched here.
        //  An UPDATE at assign time can only reach rows that already exist —
        //  it can never tag a ping that arrives five minutes from now. Tagging
        //  new pings with the active trip has to happen where they're
        //  inserted (the GPS ingestion process, or a DB trigger — see the
        //  accompanying .sql script). Doing it here either does nothing
        //  (if scoped to "now or later") or wrongly back-fills history
        //  (if scoped to "trip_id IS NULL", as the previous version did).
        //
        //  Both lock guards use ISNULL(..., 0) = 0 rather than "= 0" — real
        //  data has AssignFlag/assiged_flag as NULL (never explicitly set)
        //  for plenty of rows, and "NULL = 0" is never true in SQL, so a
        //  literal "= 0" guard would wrongly treat an untouched row as
        //  already taken and throw on a perfectly free device/trip.
        // ════════════════════════════════════════════════════════════════════
        public async Task AssignDeviceToTripAsync(int tripPk, string imei, string tripId, int battery)
        {
            const string sqlDevice =
                @"UPDATE [dbo].[gps_devices_vta]
                  SET    AssignFlag = 1
                  WHERE  imei = @Imei
                    AND  ISNULL(AssignFlag, 0) = 0";

            const string sqlTrip =
                @"UPDATE [dbo].[gps_trip_detail]
                  SET    assiged_flag = 1,
                         device_id    = @Imei,
                         battery      = @Battery,
                         assign_Date  = GETDATE()
                  WHERE  pk = @Pk
                    AND  ISNULL(assiged_flag, 0) = 0";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();
            await using var txn = conn.BeginTransaction();
            try
            {
                // Step 1 — lock device
                await using var c1 = new SqlCommand(sqlDevice, conn, txn);
                c1.Parameters.AddWithValue("@Imei", imei);
                if (await c1.ExecuteNonQueryAsync() == 0)
                    throw new InvalidOperationException(
                        "Device was just taken by someone else. Please refresh and try again.");

                // Step 2 — lock trip, stamp battery + assign date
                await using var c2 = new SqlCommand(sqlTrip, conn, txn);
                c2.Parameters.AddWithValue("@Imei", imei);
                c2.Parameters.AddWithValue("@Battery", battery);
                c2.Parameters.AddWithValue("@Pk", tripPk);
                if (await c2.ExecuteNonQueryAsync() == 0)
                    throw new InvalidOperationException(
                        "Trip was already assigned. Please refresh and try again.");

                await txn.CommitAsync();
            }
            catch
            {
                await txn.RollbackAsync();
                throw;
            }
        }

        // ════════════════════════════════════════════════════════════════════
        //  PRIVATE — map SqlDataReader row → TripDetailDto
        // ════════════════════════════════════════════════════════════════════
        private static TripDetailDto MapTrip(SqlDataReader r) => new()
        {
            Pk = Convert.ToInt32(r["pk"]),
            TripId = r["trip_ID"]?.ToString() ?? "",
            VehicleNo = r["vehicle_no"]?.ToString() ?? "",
            SerialNo = r["serial_no"]?.ToString() ?? "",
            DriverNo = r["driver_no"]?.ToString() ?? "",
            DeviceId = r["device_id"]?.ToString() ?? "",
            AssignedFlag = r["assiged_flag"] == DBNull.Value ? 0 : Convert.ToInt32(r["assiged_flag"]),
            Battery = r["battery"] == DBNull.Value ? 0 : Convert.ToInt32(r["battery"])
        };
    }
}