using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    // Requires the existing class declaration in TrackController.cs to become:
    //     public partial class TrackController : Controller
    // ([Authorize] on that declaration applies to this file too.)
    public partial class TrackController
    {
        // ── Trip picker for the Track History page ─────────────────────────
        [RequirePageAccess(PageAccess.TrackHistory)]
        [HttpGet]
        public IActionResult TripListJson(string? q, string? imei)
        {
            var trips = new List<object>();
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"
                    SELECT TOP 50 [trip_ID],[vehicle_no],[device_id],
                           ISNULL([assiged_flag],0) AS af, [close_Date]
                    FROM   [atmparking].[dbo].[gps_trip_detail]
                    WHERE  (@imei = '' OR [device_id] = @imei)
                      AND  (@q = '' OR [trip_ID] LIKE @like OR [vehicle_no] LIKE @like)
                    ORDER  BY CASE WHEN @imei <> '' AND [close_Date] IS NULL THEN 0 ELSE 1 END, [pk] DESC";
                using var cmd = new SqlCommand(sql, con);
                var term = (q ?? "").Trim();
                cmd.Parameters.AddWithValue("@q", term);
                cmd.Parameters.AddWithValue("@like", "%" + term + "%");
                cmd.Parameters.AddWithValue("@imei", (imei ?? "").Trim());
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                {
                    var closed = dr["close_Date"] != DBNull.Value;
                    var assigned = Convert.ToInt32(dr["af"]) == 1;
                    trips.Add(new
                    {
                        tripId = dr["trip_ID"].ToString(),
                        vehicleNo = dr["vehicle_no"] == DBNull.Value ? "" : dr["vehicle_no"].ToString(),
                        deviceId = dr["device_id"] == DBNull.Value ? "" : dr["device_id"].ToString(),
                        status = closed ? "closed" : assigned ? "active" : "created"
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = ex.Message, trips = Array.Empty<object>() });
            }
            return Json(new { ok = true, trips });
        }

        // ── Route + activity for ONE trip ──────────────────────────────────
        [RequirePageAccess(PageAccess.TrackHistory)]
        [HttpGet]
        public IActionResult TripHistoryJson(string tripId, string? dateFrom, string? dateTo)
        {
            if (string.IsNullOrWhiteSpace(tripId))
                return Json(new { ok = false, error = "Trip ID required" });

            try
            {
                using var con = _db.GetConnection();
                con.Open();

                // 1) Trip header
                string vehicleNo, deviceId;
                bool assigned;
                DateTime? assignDate, closeDate;
                using (var cmd = new SqlCommand(@"
                    SELECT TOP 1 [vehicle_no],[device_id],ISNULL([assiged_flag],0) AS af,[assign_Date],[close_Date]
                    FROM   [atmparking].[dbo].[gps_trip_detail] WHERE [trip_ID] = @t", con))
                {
                    cmd.Parameters.AddWithValue("@t", tripId);
                    using var dr = cmd.ExecuteReader();
                    if (!dr.Read()) return Json(new { ok = false, error = $"Trip {tripId} not found." });
                    vehicleNo = dr["vehicle_no"] == DBNull.Value ? "" : dr["vehicle_no"].ToString()!;
                    deviceId = dr["device_id"] == DBNull.Value ? "" : dr["device_id"].ToString()!;
                    assigned = Convert.ToInt32(dr["af"]) == 1;
                    assignDate = dr["assign_Date"] == DBNull.Value ? null : Convert.ToDateTime(dr["assign_Date"]);
                    closeDate = dr["close_Date"] == DBNull.Value ? null : Convert.ToDateTime(dr["close_Date"]);
                }

                // 2) Route: only pings stamped with THIS trip.
                //    (Capped at 5000 like the old history query; trips longer than that show their first 5000 pings.)
                var raw = new List<TrackPoint>();
                var hasFrom = DateTime.TryParse(dateFrom, out var fromDt);
                var hasTo = DateTime.TryParse(dateTo, out var toDt);
                var pingSql = @"
                    SELECT TOP 5000 [latitude],[longitude],[created_at],[speed]
                    FROM   [atmparking].[dbo].[gps_locations_vta]
                    WHERE  [trip_id] = @t"
                    + (hasFrom ? " AND [created_at] >= @from" : "")
                    + (hasTo ? " AND [created_at] <= @to" : "")
                    + " ORDER BY [created_at] ASC";
                using (var cmd = new SqlCommand(pingSql, con))
                {
                    cmd.Parameters.AddWithValue("@t", tripId);
                    if (hasFrom) cmd.Parameters.AddWithValue("@from", fromDt);
                    if (hasTo) cmd.Parameters.AddWithValue("@to", toDt);
                    using var dr = cmd.ExecuteReader();
                    while (dr.Read())
                    {
                        double lat = dr["latitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["latitude"]);
                        double lng = dr["longitude"] == DBNull.Value ? 0 : Convert.ToDouble(dr["longitude"]);
                        if (lat == 0 && lng == 0) continue;
                        raw.Add(new TrackPoint
                        {
                            Latitude = lat,
                            Longitude = lng,
                            Timestamp = Convert.ToDateTime(dr["created_at"]).ToString("yyyy-MM-dd HH:mm:ss"),
                            Speed = dr["speed"] == DBNull.Value ? null : (double?)Convert.ToDouble(dr["speed"])
                        });
                    }
                }
                var points = RemoveGpsOutliers(raw);

                // 3) Activity timeline
                var events = new List<(string Type, DateTime Time, double? Lat, double? Lng, string? Details)>();
                using (var cmd = new SqlCommand(@"
                    SELECT [event_type],[event_time],[latitude],[longitude],[details]
                    FROM   [atmparking].[dbo].[gps_trip_events]
                    WHERE  [trip_id] = @t
                    ORDER  BY [event_time] ASC, [id] ASC", con))
                {
                    cmd.Parameters.AddWithValue("@t", tripId);
                    using var dr = cmd.ExecuteReader();
                    while (dr.Read())
                        events.Add((
                            dr["event_type"].ToString()!,
                            Convert.ToDateTime(dr["event_time"]),
                            dr["latitude"] == DBNull.Value ? null : Convert.ToDouble(dr["latitude"]),
                            dr["longitude"] == DBNull.Value ? null : Convert.ToDouble(dr["longitude"]),
                            dr["details"] == DBNull.Value ? null : dr["details"].ToString()));
                }

                // 4) Summary: trip hours (from creation) and halts
                //    Trips created before the trigger existed have no TRIP_CREATED event → fall back to assign date.
                DateTime? started = events.Where(e => e.Type == "TRIP_CREATED").Select(e => (DateTime?)e.Time).FirstOrDefault()
                                    ?? assignDate;
                var end = closeDate ?? DateTime.Now;
                double? hours = started.HasValue ? Math.Round((end - started.Value).TotalHours, 2) : null;

                int haltCount = 0;
                double haltMinutes = 0;
                DateTime? openHalt = null;
                foreach (var e in events)
                {
                    if (e.Type == "HALT_START") { haltCount++; openHalt = e.Time; }
                    else if (e.Type == "HALT_END" && openHalt.HasValue)
                    {
                        haltMinutes += (e.Time - openHalt.Value).TotalMinutes;
                        openHalt = null;
                    }
                }
                if (openHalt.HasValue) haltMinutes += (end - openHalt.Value).TotalMinutes;

                return Json(new
                {
                    ok = true,
                    trip = new
                    {
                        tripId,
                        vehicleNo,
                        deviceId,
                        status = closeDate.HasValue ? "closed" : assigned ? "active" : "created",
                        startedAt = started,
                        endedAt = closeDate
                    },
                    points,
                    events = events.Select(e => new { type = e.Type, time = e.Time, lat = e.Lat, lng = e.Lng, details = e.Details }),
                    summary = new { hours, haltCount, haltMinutes = Math.Round(haltMinutes) },
                    message = points.Count == 0
                        ? (hasFrom || hasTo ? "No GPS points for this trip in the selected date range."
                           : assigned ? "No route yet — this trip hasn't recorded any GPS points."
                                      : "No route yet — no device has been assigned to this trip.")
                        : ""
                });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, error = "Error: " + ex.Message });
            }
        }

        // Old links (Dashboard "▶ Play", etc.) still pass ?imei= — resolve to that device's trip.
        // Prefers the open trip; falls back to the device's most recent one.
        private string? FindTripForDevice(string imei)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                using var cmd = new SqlCommand(@"
                    SELECT TOP 1 [trip_ID]
                    FROM   [atmparking].[dbo].[gps_trip_detail]
                    WHERE  [device_id] = @i
                    ORDER  BY CASE WHEN [close_Date] IS NULL THEN 0 ELSE 1 END, [pk] DESC", con);
                cmd.Parameters.AddWithValue("@i", imei);
                return cmd.ExecuteScalar()?.ToString();
            }
            catch { return null; }
        }
    }
}
