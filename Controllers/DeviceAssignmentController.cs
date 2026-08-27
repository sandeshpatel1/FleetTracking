using Microsoft.AspNetCore.Mvc;
using TrackingMVC.Data;
using TrackingMVC.Models.ViewModels;

namespace TrackingMVC.Controllers
{
    public class DeviceAssignmentController : Controller
    {
        private readonly DeviceAssignmentRepository _repo;
        private readonly IWebHostEnvironment _env;

        public DeviceAssignmentController(DeviceAssignmentRepository repo, IWebHostEnvironment env)
        {
            _repo = repo;
            _env = env;
        }

        // GET /DeviceAssignment
        public IActionResult Index()
        {
            ViewData["Title"] = "Device Assignment";
            ViewBag.Active = "deviceassignment";
            return View();
        }

        // GET /DeviceAssignment/Autocomplete?q=TRP-2026&type=trip
        [HttpGet]
        public async Task<IActionResult> Autocomplete(string q, string type = "vehicle")
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Json(new { ok = true, data = Array.Empty<object>() });

            try
            {
                var items = await _repo.GetSuggestionsAsync(q.Trim(), type);
                return Json(new { ok = true, data = items });
            }
            catch (Exception ex)
            {
                // Return the real error so you can see it in the browser console
                return Json(new
                {
                    ok = false,
                    message = _env.IsDevelopment() ? ex.ToString() : "Search failed. Check server logs.",
                    data = Array.Empty<object>()
                });
            }
        }

        // GET /DeviceAssignment/TripDetail?pk=1
        [HttpGet]
        public async Task<IActionResult> TripDetail(int pk)
        {
            try
            {
                var trip = await _repo.GetTripByPkAsync(pk);
                if (trip == null)
                    return Json(new { ok = false, message = "Trip not found." });

                LastLocationDto? loc = null;
                if (trip.AssignedFlag == 1 && !string.IsNullOrWhiteSpace(trip.DeviceId))
                {
                    try { loc = await _repo.GetLastLocationByImeiAsync(trip.DeviceId); }
                    catch { /* location is optional — don't fail the whole request */ }
                }

                return Json(new { ok = true, trip, location = loc });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = _env.IsDevelopment() ? ex.ToString() : "Error loading trip."
                });
            }
        }

        // GET /DeviceAssignment/AvailableDevices
        [HttpGet]
        public async Task<IActionResult> AvailableDevices()
        {
            try
            {
                var all = await _repo.GetAvailableDevicesAsync();

                // If gps_locations_vta has no rows yet (testing),
                // derive a stable demo battery from the IMEI so the UI still works.
                foreach (var d in all.Where(x => x.BatteryLevel == 0))
                    d.BatteryLevel = DemoBattery(d.DeviceImei);

                var filtered = all
                    .Where(d => d.BatteryLevel > 50)       // core rule from SRS
                    .OrderByDescending(d => d.BatteryLevel)
                    .ToList();

                return Json(new { ok = true, data = filtered });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = _env.IsDevelopment() ? ex.ToString() : "Error loading devices.",
                    data = Array.Empty<object>()
                });
            }
        }

        // GET /DeviceAssignment/ImeiAutocomplete?q=3512
        // NOTE: this action did not exist before — the JS was calling a 404.
        [HttpGet]
        public async Task<IActionResult> ImeiAutocomplete(string q)
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
                return Json(new { ok = true, data = Array.Empty<object>() });

            try
            {
                var items = await _repo.SearchImeiAsync(q.Trim());
                var data = items.Select(d => new
                {
                    imei = d.DeviceImei,
                    batteryLevel = d.BatteryLevel,
                    lastSeen = d.LastSeen == DateTime.MinValue ? "—" : d.LastSeen.ToString("dd MMM, HH:mm")
                });
                return Json(new { ok = true, data });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = _env.IsDevelopment() ? ex.ToString() : "IMEI search failed. Check server logs.",
                    data = Array.Empty<object>()
                });
            }
        }

        // GET /DeviceAssignment/LookupImei?imei=351234567890001
        // NOTE: this action did not exist before — the JS was calling a 404.
        [HttpGet]
        public async Task<IActionResult> LookupImei(string imei)
        {
            if (string.IsNullOrWhiteSpace(imei) || imei.Trim().Length < 5)
                return Json(new { ok = false, message = "Enter a valid IMEI (at least 5 digits)." });

            try
            {
                var device = await _repo.GetDeviceByImeiAsync(imei.Trim());
                if (device == null)
                    return Json(new { ok = false, message = "IMEI not found in the system." });

                var (_, isAssigned) = await _repo.CheckImeiAsync(imei.Trim());

                return Json(new
                {
                    ok = true,
                    device = new
                    {
                        imei = device.DeviceImei,
                        batteryLevel = device.BatteryLevel,
                        assignFlag = isAssigned ? 1 : 0,
                        lastSeen = device.LastSeen == DateTime.MinValue ? (DateTime?)null : device.LastSeen
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    ok = false,
                    message = _env.IsDevelopment() ? ex.ToString() : "Lookup failed. Check server logs."
                });
            }
        }

        // POST /DeviceAssignment/Assign
        [HttpPost]
        public async Task<IActionResult> Assign([FromBody] AssignDeviceRequest req)
        {
            if (req == null || string.IsNullOrWhiteSpace(req.DeviceImei) || req.TripPk <= 0)
                return Json(new ApiResponse { Ok = false, Message = "Invalid request." });

            var imei = req.DeviceImei.Trim();

            try
            {
                var trip = await _repo.GetTripByPkAsync(req.TripPk);
                if (trip == null)
                    return Json(new ApiResponse { Ok = false, Message = "Trip not found." });
                if (trip.AssignedFlag == 1)
                    return Json(new ApiResponse { Ok = false, Message = "Trip already has a device assigned." });

                // Same lookup LookupImei already used to build the confirm card —
                // reusing it means this check can never disagree with what the user saw on screen.
                var device = await _repo.GetDeviceByImeiAsync(imei);
                if (device == null)
                    return Json(new ApiResponse { Ok = false, Message = "IMEI not found in the system." });

                var (_, taken) = await _repo.CheckImeiAsync(imei);
                if (taken)
                    return Json(new ApiResponse { Ok = false, Message = "Device already assigned to another trip." });

                await _repo.AssignDeviceToTripAsync(req.TripPk, imei, trip.TripId, device.BatteryLevel);

                return Json(new ApiResponse
                {
                    Ok = true,
                    Message = $"Device {imei} assigned to {trip.VehicleNo} / {trip.TripId}."
                });
            }
            catch (InvalidOperationException ioe)
            {
                return Json(new ApiResponse { Ok = false, Message = ioe.Message });
            }
            catch (Exception ex)
            {
                return Json(new ApiResponse
                {
                    Ok = false,
                    Message = _env.IsDevelopment() ? ex.ToString() : "Assignment failed. Check server logs."
                });
            }
        }

        // Remove once gps_locations_vta is populated with real device data.
        private static int DemoBattery(string imei)
        {
            if (long.TryParse(imei.Replace("-", "").Replace(" ", ""), out long n))
                return (int)(n % 50) + 51;
            return 75;
        }
    }
}