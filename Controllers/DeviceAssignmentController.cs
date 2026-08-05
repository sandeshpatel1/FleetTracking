using Microsoft.AspNetCore.Mvc;
using TrackingMVC.Data;
using TrackingMVC.Models.ViewModels;

namespace TrackingMVC.Controllers
{
    public class DeviceAssignmentController : Controller
    {
        private readonly DeviceAssignmentRepository _repo;

        public DeviceAssignmentController(DeviceAssignmentRepository repo)
        {
            _repo = repo;
        }

        // ── Page ──────────────────────────────────────────────────────────────
        // GET /DeviceAssignment
        public IActionResult Index()
        {
            ViewData["Title"] = "Device Assignment";
            ViewData["ActivePage"] = "DeviceAssignment";
            return View();
        }

        // ── API: Search trips by vehicle number ───────────────────────────────
        // GET /DeviceAssignment/SearchTrip?vehicleNo=MH04AB1234
        [HttpGet]
        public async Task<IActionResult> SearchTrip(string vehicleNo)
        {
            if (string.IsNullOrWhiteSpace(vehicleNo))
                return Json(new { ok = false, message = "Vehicle number is required." });

            try
            {
                var trips = await _repo.SearchTripsByVehicleAsync(vehicleNo);
                return Json(new { ok = true, data = trips });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = "Database error: " + ex.Message });
            }
        }

        // ── API: List available devices (battery > 50 %, AssignFlag = 0) ──────
        // GET /DeviceAssignment/AvailableDevices
        [HttpGet]
        public async Task<IActionResult> AvailableDevices()
        {
            try
            {
                // 1. Pull unassigned IMEIs from DB
                var rawList = await _repo.GetAvailableImeiListAsync();

                if (!rawList.Any())
                    return Json(new { ok = true, data = new List<AvailableDeviceDto>() });

                // 2. Fetch battery levels (replace demo method with real query)
                var imeis = rawList.Select(d => d.DeviceImei);
                var batteries = await _repo.GetBatteryLevelsAsync(imeis);

                // 3. Merge battery into DTO & filter battery > 50 %
                var signals = new[] { "Excellent", "Good", "Good", "Fair", "Excellent" };
                var rng = new Random();

                var available = rawList
                    .Select(d =>
                    {
                        d.BatteryLevel = batteries.TryGetValue(d.DeviceImei, out int b) ? b : 0;
                        d.SignalStrength = signals[Math.Abs(d.DeviceImei.GetHashCode()) % signals.Length];
                        d.LastSeen = DateTime.Now.AddMinutes(-rng.Next(1, 25));
                        return d;
                    })
                    .Where(d => d.BatteryLevel > 50)          // ← SRS rule: battery > 50 %
                    .OrderByDescending(d => d.BatteryLevel)
                    .ToList();

                return Json(new { ok = true, data = available });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = "Database error: " + ex.Message });
            }
        }

        // ── API: Assign device to trip ────────────────────────────────────────
        // POST /DeviceAssignment/Assign
        [HttpPost]
        public async Task<IActionResult> Assign([FromBody] AssignDeviceRequest req)
        {
            if (req == null
                || string.IsNullOrWhiteSpace(req.DeviceImei)
                || req.TripPk <= 0)
                return Json(new ApiResponse { Ok = false, Message = "Invalid request data." });

            try
            {
                // Validate trip still exists & unassigned
                var trip = await _repo.GetTripByPkAsync(req.TripPk);
                if (trip == null)
                    return Json(new ApiResponse { Ok = false, Message = "Trip not found." });
                if (trip.AssignedFlag == 1)
                    return Json(new ApiResponse { Ok = false, Message = "This trip already has a device assigned." });

                // Validate IMEI exists & still free
                var (exists, isAssigned) = await _repo.CheckImeiAsync(req.DeviceImei);
                if (!exists)
                    return Json(new ApiResponse { Ok = false, Message = "Device IMEI not found in the system." });
                if (isAssigned)
                    return Json(new ApiResponse { Ok = false, Message = "This device is already assigned to another trip." });

                // Atomic DB update
                await _repo.AssignDeviceToTripAsync(req.TripPk, req.DeviceImei);

                return Json(new ApiResponse
                {
                    Ok = true,
                    Message = $"Device {req.DeviceImei} successfully assigned to trip {trip.TripId} / vehicle {trip.VehicleNo}."
                });
            }
            catch (InvalidOperationException ioe)
            {
                // Optimistic-lock race condition messages from the repo
                return Json(new ApiResponse { Ok = false, Message = ioe.Message });
            }
            catch (Exception ex)
            {
                return Json(new ApiResponse { Ok = false, Message = "Database error: " + ex.Message });
            }
        }
    }
}