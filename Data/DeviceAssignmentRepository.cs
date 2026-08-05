using Microsoft.Data.SqlClient;
using Newtonsoft.Json.Linq;
using TrackingMVC.Models.ViewModels;

namespace TrackingMVC.Data
{
    /// <summary>
    /// All database access for the Device-Assignment feature.
    /// Uses raw ADO.NET via DbHelper — no Entity Framework.
    ///
    /// Tables:
    ///   [sa_lio].[gps_trip_detail]       pk | trip_ID | vehicle_no | serial_no | assiged_flag | driver_no | device_id
    ///   [sa_lio].[gps_Device_Imei_List]  pk | device_Imei | AssignFlag
    /// </summary>
    public class DeviceAssignmentRepository
    {
        private readonly DbHelper _db;

        public DeviceAssignmentRepository(DbHelper db)
        {
            _db = db;
        }

        // ── 1. Search trips by vehicle number ─────────────────────────────────
        public async Task<List<TripSearchResult>> SearchTripsByVehicleAsync(string vehicleNo)
        {
            const string sql = @"
                SELECT TOP 20
                    pk, trip_ID, vehicle_no, serial_no,
                    assiged_flag, driver_no, ISNULL(device_id,'') AS device_id
                FROM [sa_lio].[gps_trip_detail]
                WHERE vehicle_no LIKE @VehicleNo
                ORDER BY pk DESC";

            var results = new List<TripSearchResult>();

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@VehicleNo", "%" + vehicleNo.Trim() + "%");

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new TripSearchResult
                {
                    Pk = reader.GetInt32(reader.GetOrdinal("pk")),
                    TripId = reader["trip_ID"]?.ToString() ?? "",
                    VehicleNo = reader["vehicle_no"]?.ToString() ?? "",
                    SerialNo = reader["serial_no"]?.ToString() ?? "",
                    AssignedFlag = reader.GetInt32(reader.GetOrdinal("assiged_flag")),
                    DriverNo = reader["driver_no"]?.ToString() ?? "",
                    DeviceId = reader["device_id"]?.ToString() ?? ""
                });
            }
            return results;
        }

        // ── 2. Get available devices: AssignFlag = 0 ──────────────────────────
        //    Battery is read from gps_Device_Imei_List if you add a BatteryLevel
        //    column there, or from a separate battery table.
        //    For now the query reads every unassigned IMEI and the controller
        //    filters battery > 50 after fetching the live battery level.
        public async Task<List<AvailableDeviceDto>> GetAvailableImeiListAsync()
        {
            // ── If you have a battery column in gps_Device_Imei_List ──────────
            // change the SELECT to include it, e.g.:
            //   SELECT pk, device_Imei, ISNULL(BatteryLevel,0) AS BatteryLevel
            // For now we select the raw list and derive battery in code.
            const string sql = @"
                SELECT pk, device_Imei
                FROM [sa_lio].[gps_Device_Imei_List]
                WHERE AssignFlag = 0
                ORDER BY pk";

            var results = new List<AvailableDeviceDto>();

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results.Add(new AvailableDeviceDto
                {
                    Pk = reader.GetInt32(reader.GetOrdinal("pk")),
                    DeviceImei = reader["device_Imei"]?.ToString() ?? "",
                    // Battery & signal resolved by the controller / a real battery query
                    BatteryLevel = 0,
                    SignalStrength = "Good",
                    LastSeen = DateTime.Now
                });
            }
            return results;
        }

        // ── 3. Get live battery for a list of IMEIs ───────────────────────────
        //    Replace the body of this method with a real query once your
        //    battery table is known (e.g. gps_device_battery / gps_locations_vta).
        //    The method signature stays the same so the controller needs no change.
        public async Task<Dictionary<string, int>> GetBatteryLevelsAsync(IEnumerable<string> imeis)
        {
            // ─── REAL QUERY TEMPLATE (uncomment & adjust table/column names) ───
            //
            // const string sql = @"
            //     SELECT device_imei, battery_level
            //     FROM [sa_lio].[gps_locations_vta]
            //     WHERE device_imei IN (
            //         SELECT value FROM STRING_SPLIT(@Imeis, ',')
            //     )
            //     AND recorded_at = (
            //         SELECT MAX(recorded_at)
            //         FROM [sa_lio].[gps_locations_vta] t2
            //         WHERE t2.device_imei = gps_locations_vta.device_imei
            //     )";
            //
            // await using var conn = _db.GetConnection();
            // await conn.OpenAsync();
            // await using var cmd = new SqlCommand(sql, conn);
            // cmd.Parameters.AddWithValue("@Imeis", string.Join(",", imeis));
            // var dict = new Dictionary<string, int>();
            // await using var reader = await cmd.ExecuteReaderAsync();
            // while (await reader.ReadAsync())
            //     dict[reader["device_imei"].ToString()!] = Convert.ToInt32(reader["battery_level"]);
            // return dict;

            // ─── DEMO: deterministic battery from IMEI digits (remove when real) ───
            await Task.CompletedTask;   // keep method async
            return imeis.ToDictionary(
                imei => imei,
                imei =>
                {
                    if (long.TryParse(
                            imei.Replace("-", "").Replace(" ", ""),
                            out long num))
                        return (int)(num % 50) + 51;   // always 51-100
                    return 75;
                });
        }

        // ── 4. Check if a trip exists and is still unassigned ─────────────────
        public async Task<TripSearchResult?> GetTripByPkAsync(int pk)
        {
            const string sql = @"
                SELECT pk, trip_ID, vehicle_no, serial_no,
                       assiged_flag, driver_no, ISNULL(device_id,'') AS device_id
                FROM [sa_lio].[gps_trip_detail]
                WHERE pk = @Pk";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Pk", pk);

            await using var reader = await cmd.ExecuteReaderAsync();
            if (!await reader.ReadAsync()) return null;

            return new TripSearchResult
            {
                Pk = reader.GetInt32(reader.GetOrdinal("pk")),
                TripId = reader["trip_ID"]?.ToString() ?? "",
                VehicleNo = reader["vehicle_no"]?.ToString() ?? "",
                SerialNo = reader["serial_no"]?.ToString() ?? "",
                AssignedFlag = reader.GetInt32(reader.GetOrdinal("assiged_flag")),
                DriverNo = reader["driver_no"]?.ToString() ?? "",
                DeviceId = reader["device_id"]?.ToString() ?? ""
            };
        }

        // ── 5. Check IMEI availability ────────────────────────────────────────
        public async Task<(bool Exists, bool IsAssigned)> CheckImeiAsync(string imei)
        {
            const string sql = @"
                SELECT AssignFlag
                FROM [sa_lio].[gps_Device_Imei_List]
                WHERE device_Imei = @Imei";

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();

            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@Imei", imei);

            var result = await cmd.ExecuteScalarAsync();
            if (result == null || result == DBNull.Value)
                return (false, false);

            return (true, Convert.ToInt32(result) == 1);
        }

        // ── 6. Perform the assignment (transaction) ───────────────────────────
        //    Locks both rows atomically:
        //      gps_Device_Imei_List.AssignFlag  = 1
        //      gps_trip_detail.assiged_flag     = 1
        //      gps_trip_detail.device_id        = @Imei
        public async Task<bool> AssignDeviceToTripAsync(int tripPk, string imei)
        {
            const string sqlImei = @"
                UPDATE [sa_lio].[gps_Device_Imei_List]
                SET    AssignFlag = 1
                WHERE  device_Imei = @Imei
                  AND  AssignFlag  = 0";

            const string sqlTrip = @"
                UPDATE [sa_lio].[gps_trip_detail]
                SET    assiged_flag = 1,
                       device_id   = @Imei
                WHERE  pk           = @Pk
                  AND  assiged_flag = 0"; 

            await using var conn = _db.GetConnection();
            await conn.OpenAsync();

            await using var txn = conn.BeginTransaction();
            try
            {
                // Lock & update the IMEI row
                await using var cmdImei = new SqlCommand(sqlImei, conn, txn);
                cmdImei.Parameters.AddWithValue("@Imei", imei);
                int rowsImei = await cmdImei.ExecuteNonQueryAsync();
                if (rowsImei == 0)
                    throw new InvalidOperationException(
                        "Device is no longer available — it may have been assigned by another user.");

                // Lock & update the trip row
                await using var cmdTrip = new SqlCommand(sqlTrip, conn, txn);
                cmdTrip.Parameters.AddWithValue("@Imei", imei);
                cmdTrip.Parameters.AddWithValue("@Pk", tripPk);
                int rowsTrip = await cmdTrip.ExecuteNonQueryAsync();
                if (rowsTrip == 0)
                    throw new InvalidOperationException(
                        "Trip has already been assigned — please refresh and try again.");

                await txn.CommitAsync();
                return true;
            }
            catch
            {
                await txn.RollbackAsync();
                throw;
            }
        }
    }
}