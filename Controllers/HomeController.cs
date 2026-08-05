using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    [RequireLogin]
    public class HomeController : Controller
    {
        private readonly DbHelper _db;
        private readonly IConfiguration _cfg;

        public HomeController(DbHelper db, IConfiguration cfg) { _db = db; _cfg = cfg; }

        public IActionResult Index()
        {
            var vm = LoadDashboard();
            ViewBag.MapsKey = _cfg["AppSettings:GoogleMapsApiKey"];
            ViewBag.Active = "dashboard";
            return View(vm);
        }

        // JSON endpoint for live map (called every 30s)
        [HttpGet]
        public IActionResult DevicesJson()
        {
            try
            {
                var vm = LoadDashboard();
                return Json(new { ok = true, assets = vm.Devices, error = "" });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, assets = new List<object>(), error = ex.Message });
            }
        }

        // JSON endpoint for dashboard map (geofences + parking)
        [HttpGet]
        public IActionResult DashboardMapJson()
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                var geoFences = LoadGeoFences(con);
                var parking = LoadParkingPoints(con);
                var devices = LoadDeviceList(con, geoFences);
                return Json(new { ok = true, geoFences, parking, devices, error = "" });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message });
            }
        }

        // ── Shared loader ─────────────────────────────────────
        private DashboardViewModel LoadDashboard()
        {
            var vm = new DashboardViewModel();
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                vm.GeoFences = LoadGeoFences(con);
                vm.ParkingPoints = LoadParkingPoints(con);
                vm.Devices = LoadDeviceList(con, vm.GeoFences);
            }
            catch (Exception ex)
            {
                ViewBag.DbError = ex.Message;
            }
            vm.TotalDevices = vm.Devices.Count;
            vm.OnlineDevices = vm.Devices.Count(d => d.Status == "online");
            vm.OfflineDevices = vm.Devices.Count(d => d.Status == "offline");
            return vm;
        }

        // Devices list — name/geofence assignment kept as-is (index-matched to geofence),
        // but latitude/longitude/last-seen/status now come from the REAL GPS data in
        // gps_locations_vta for that device's imei, instead of the static geofence
        // coordinate. This is the fix for "not showing current live location".
        private List<DeviceAsset> LoadDeviceList(SqlConnection con, List<GeoFenceLocation> geoList)
        {
            var list = new List<DeviceAsset>();
            var latest = LoadLatestPositions(con);

            const string sql = "SELECT [id],[imei],[last_seen] FROM [Sunmoon_Enterprises].[sa_lio].[gps_devices_vta] ORDER BY [id]";
            using var cmd = new SqlCommand(sql, con);
            using var dr = cmd.ExecuteReader();
            int idx = 0;
            while (dr.Read())
            {
                var geo = idx < geoList.Count
                    ? geoList[idx]
                    : new GeoFenceLocation { Latitude = 19.0760, Longitude = 72.8777, Name = "Unknown" };
                idx++;

                var imei = dr["imei"].ToString()!;
                DateTime? deviceLastSeen = dr["last_seen"] == DBNull.Value ? null : Convert.ToDateTime(dr["last_seen"]);

                // Default fallback: geofence's static coordinate (used only if this
                // device has NEVER once reported a valid GPS fix).
                double lat = geo.Latitude, lng = geo.Longitude;
                DateTime? pingTime = deviceLastSeen;

                if (latest.TryGetValue(imei, out var pos))
                {
                    // Position = the last VALID GPS fix. This is what "live feed"
                    // means here: it holds steady at the last known coordinate
                    // whenever newer packets are heartbeats with no GPS lock,
                    // instead of snapping back to a static location.
                    lat = pos.Latitude;
                    lng = pos.Longitude;

                    // Online/offline is based on the last ping of ANY kind
                    // (including no-GPS heartbeat packets), since those still
                    // prove the device is alive and communicating.
                    pingTime = pos.LastPing;
                }

                bool online = pingTime.HasValue && (DateTime.Now - pingTime.Value).TotalMinutes <= 30;

                list.Add(new DeviceAsset
                {
                    Id = Convert.ToInt32(dr["id"]),
                    Imei = imei,
                    Name = geo.Name,
                    Latitude = lat,
                    Longitude = lng,
                    Geofence = geo.RadiusMeters,
                    GeoFenceId = geo.Id,
                    LastSeen = pingTime.HasValue ? pingTime.Value.ToString("yyyy-MM-dd HH:mm:ss") : "Unknown",
                    Status = online ? "online" : "offline"
                });
            }
            return list;
        }

        // For each imei, returns the last VALID GPS fix (latitude/longitude not
        // null) plus the last ping of any kind (including heartbeat-only packets
        // with no GPS lock). Position uses the former; online/offline status
        // uses the latter.
        private static Dictionary<string, (double Latitude, double Longitude, DateTime FixTime, DateTime LastPing)> LoadLatestPositions(SqlConnection con)
        {
            var map = new Dictionary<string, (double, double, DateTime, DateTime)>(StringComparer.OrdinalIgnoreCase);
            const string sql = @"
                ;WITH latest_fix AS (
                    SELECT [imei], [latitude], [longitude], [created_at],
                           ROW_NUMBER() OVER (PARTITION BY [imei] ORDER BY [created_at] DESC) AS rn
                    FROM   [Sunmoon_Enterprises].[sa_lio].[gps_locations_vta]
                    WHERE  [latitude] IS NOT NULL AND [longitude] IS NOT NULL
                ),
                latest_ping AS (
                    SELECT [imei], MAX([created_at]) AS last_ping
                    FROM   [Sunmoon_Enterprises].[sa_lio].[gps_locations_vta]
                    GROUP BY [imei]
                )
                SELECT f.[imei], f.[latitude], f.[longitude], f.[created_at] AS fix_time, p.[last_ping]
                FROM   latest_fix f
                JOIN   latest_ping p ON p.[imei] = f.[imei]
                WHERE  f.rn = 1";
            using var cmd = new SqlCommand(sql, con);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
            {
                if (dr["imei"] == DBNull.Value) continue;
                if (dr["latitude"] == DBNull.Value || dr["longitude"] == DBNull.Value) continue;

                var imei = dr["imei"].ToString()!;
                var lat = Convert.ToDouble(dr["latitude"]);
                var lng = Convert.ToDouble(dr["longitude"]);

                // Ignore obvious bad GPS fixes (0,0 "null island").
                if (lat == 0 && lng == 0) continue;

                var fixTime = Convert.ToDateTime(dr["fix_time"]);
                var lastPing = dr["last_ping"] == DBNull.Value ? fixTime : Convert.ToDateTime(dr["last_ping"]);

                map[imei] = (lat, lng, fixTime, lastPing);
            }
            return map;
        }

        private static List<GeoFenceLocation> LoadGeoFences(SqlConnection con)
        {
            var list = new List<GeoFenceLocation>();
            const string sql = "SELECT [id],[latitude],[longitude],[name],[geofence] FROM [Sunmoon_Enterprises].[sa_lio].[gps_geo_fencing_location] ORDER BY [id]";
            using var cmd = new SqlCommand(sql, con);
            using var dr = cmd.ExecuteReader();
            while (dr.Read())
                list.Add(new GeoFenceLocation
                {
                    Id = Convert.ToInt32(dr["id"]),
                    Latitude = Convert.ToDouble(dr["latitude"]),
                    Longitude = Convert.ToDouble(dr["longitude"]),
                    Name = dr["name"].ToString()!,
                    RadiusMeters = dr["geofence"] == DBNull.Value ? 0 : Convert.ToInt32(dr["geofence"])
                });
            return list;
        }

        private static List<ParkingPoint> LoadParkingPoints(SqlConnection con)
        {
            var list = new List<ParkingPoint>();
            try
            {
                const string sql = @"
                    SELECT [id],[latitude],[longitude],[parking_location],[created_at]
                    FROM   [Sunmoon_Enterprises].[sa_lio].[gps_parking_location_coordinates]
                    ORDER  BY [created_at] DESC";
                using var cmd = new SqlCommand(sql, con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(new ParkingPoint
                    {
                        Id = Convert.ToInt32(dr["id"]),
                        Latitude = Convert.ToDouble(dr["latitude"]),
                        Longitude = Convert.ToDouble(dr["longitude"]),
                        ParkingLocationId = dr["parking_location"] == DBNull.Value ? 0 : Convert.ToInt32(dr["parking_location"]),
                        CreatedAt = dr["created_at"] == DBNull.Value ? "" : Convert.ToDateTime(dr["created_at"]).ToString("yyyy-MM-dd HH:mm:ss")
                    });
            }
            catch { /* table may not exist yet */ }
            return list;
        }
    }
}