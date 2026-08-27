using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    [RequireLogin]
    public class TrackController : Controller
    {
        private readonly DbHelper _db;
        private readonly IConfiguration _cfg;

        // A GPS fix that implies a road speed above this is treated as a bad fix
        // (satellite glitch / cold-start jump) and dropped, rather than being drawn
        // as a real leg of the route. This is what was causing the "last point is
        // from another part of the country" jump on playback.
        private const double MaxPlausibleSpeedKmh = 180;

        public TrackController(DbHelper db, IConfiguration cfg) { _db = db; _cfg = cfg; }

        // ── Live tracking map (all devices) ───────────────────
        public IActionResult Map()
        {
            ViewBag.MapsKey = _cfg["AppSettings:GoogleMapsApiKey"];
            ViewBag.Active = "map";
            return View();
        }

        // ── Track History / PlayBack ───────────────────────────
        [HttpGet]
        public IActionResult Play(string? imei, string? dateFrom, string? dateTo)
        {
            ViewBag.MapsKey = _cfg["AppSettings:GoogleMapsApiKey"];
            ViewBag.Active = "trackplay";
            var vm = new TrackViewModel { Imei = imei, DateFrom = dateFrom, DateTo = dateTo };

            if (!string.IsNullOrWhiteSpace(imei))
            {
                vm.Points = LoadHistory(imei, dateFrom, dateTo, out var err);
                vm.Error = err;
            }
            return View(vm);
        }

        [HttpPost]
        [ActionName("Play")]
        [ValidateAntiForgeryToken]
        public IActionResult PlayPost(string imei, string? dateFrom, string? dateTo)
        {
            return RedirectToAction("Play", new { imei, dateFrom, dateTo });
        }

        // ── JSON: distinct IMEI list for dropdown / autocomplete ──
        [HttpGet]
        public IActionResult ImeiListJson()
        {
            var list = new List<string>();
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"
                    SELECT DISTINCT [imei]
                    FROM   [atmparking].[dbo].[gps_locations_vta]
                    WHERE  [imei] IS NOT NULL AND [imei] <> ''
                    ORDER  BY [imei]";
                using var cmd = new SqlCommand(sql, con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    list.Add(dr["imei"].ToString()!);
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message, imeis = Array.Empty<string>() });
            }
            return Json(new { ok = true, imeis = list });
        }

        // ── JSON for JS fetch (history points) ────────────────
        // dateFrom / dateTo are now full date+time strings (e.g. from a
        // datetime-local input: "2026-07-28T04:00"), so playback can be scoped
        // down to the exact hour/minute range the user wants to see.
        [HttpGet]
        public IActionResult HistoryJson(string imei, string? dateFrom, string? dateTo)
        {
            if (string.IsNullOrWhiteSpace(imei))
                return Json(new { ok = false, error = "IMEI required", points = Array.Empty<object>() });

            var pts = LoadHistory(imei, dateFrom, dateTo, out var error);
            return Json(new { ok = string.IsNullOrEmpty(error), points = pts, error = error ?? "" });
        }

        // ── DB loader ─────────────────────────────────────────
        private List<TrackPoint> LoadHistory(string imei, string? dateFrom, string? dateTo, out string? error)
        {
            error = null;
            var raw = new List<TrackPoint>();
            try
            {
                using var con = _db.GetConnection();
                con.Open();

                string dateFilter = " AND [imei] = @imei";
                if (!string.IsNullOrWhiteSpace(dateFrom)) dateFilter += " AND [created_at] >= @from";
                if (!string.IsNullOrWhiteSpace(dateTo)) dateFilter += " AND [created_at] <= @to";

                string sql = $@"
                    SELECT TOP 2000
                           [latitude],
                           [longitude],
                           [created_at]  AS [timestamp],
                           [speed],
                           [battery]
                    FROM   [atmparking].[dbo].[gps_locations_vta]
                    WHERE  1=1 {dateFilter}
                    ORDER  BY [created_at] ASC";

                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@imei", imei);
                if (!string.IsNullOrWhiteSpace(dateFrom)) cmd.Parameters.AddWithValue("@from", DateTime.Parse(dateFrom));
                if (!string.IsNullOrWhiteSpace(dateTo)) cmd.Parameters.AddWithValue("@to", DateTime.Parse(dateTo));

                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    double lat = dr["latitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["latitude"]);
                    double lng = dr["longitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["longitude"]);

                    // Skip obvious bad GPS fixes ("null island" / no fix yet).
                    if (lat == 0 && lng == 0) continue;

                    raw.Add(new TrackPoint
                    {
                        Latitude = lat,
                        Longitude = lng,
                        Timestamp = dr["timestamp"] == DBNull.Value ? "" : Convert.ToDateTime(dr["timestamp"]).ToString("yyyy-MM-dd HH:mm:ss"),
                        Speed = dr["speed"] == DBNull.Value ? null : (double?)Convert.ToDouble(dr["speed"])
                    });
                }

                if (raw.Count == 0)
                {
                    error = $"No location history found for IMEI: {imei}" +
                            (string.IsNullOrWhiteSpace(dateFrom) ? "" : " in the selected date/time range.");
                    return raw;
                }
            }
            catch (Exception ex)
            {
                error = "Error: " + ex.Message;
                return raw;
            }

            // Remove GPS "teleport" glitches: a fix that implies unrealistic road
            // speed relative to the previous KEPT point is dropped rather than
            // being drawn as a real leg of the route. This is what stops the
            // playback line/marker from jumping to another part of the country.
            var cleaned = RemoveGpsOutliers(raw);
            if (cleaned.Count == 0)
                error = $"All location points for IMEI {imei} in this range failed GPS sanity checks.";

            return cleaned;
        }

        private static List<TrackPoint> RemoveGpsOutliers(List<TrackPoint> raw)
        {
            if (raw.Count < 3) return raw;

            var cleaned = new List<TrackPoint> { raw[0] };
            foreach (var p in raw.Skip(1))
            {
                var prev = cleaned[^1];
                if (!DateTime.TryParse(prev.Timestamp, out var t1) || !DateTime.TryParse(p.Timestamp, out var t2))
                {
                    cleaned.Add(p);
                    continue;
                }

                var hours = Math.Max((t2 - t1).TotalHours, 1.0 / 3600); // avoid divide-by-zero on same-second pings
                var distKm = Haversine(prev.Latitude, prev.Longitude, p.Latitude, p.Longitude);
                var impliedSpeedKmh = distKm / hours;

                if (impliedSpeedKmh > MaxPlausibleSpeedKmh)
                    continue; // drop — GPS glitch, not a real move

                cleaned.Add(p);
            }
            return cleaned;
        }

        private static double Haversine(double la1, double lo1, double la2, double lo2)
        {
            const double R = 6371;
            double dLat = (la2 - la1) * Math.PI / 180;
            double dLon = (lo2 - lo1) * Math.PI / 180;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(la1 * Math.PI / 180) * Math.Cos(la2 * Math.PI / 180) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}