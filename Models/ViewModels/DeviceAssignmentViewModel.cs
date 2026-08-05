namespace TrackingMVC.Models.ViewModels
{
    // ── Trip search result ────────────────────────────────────────────────────
    public class TripSearchResult
    {
        public int Pk { get; set; }
        public string TripId { get; set; } = string.Empty;
        public string VehicleNo { get; set; } = string.Empty;
        public string SerialNo { get; set; } = string.Empty;
        public string DriverNo { get; set; } = string.Empty;
        public string DeviceId { get; set; } = string.Empty;
        public int AssignedFlag { get; set; }
    }

    // ── Available GPS device (battery > 50 %, not yet assigned) ──────────────
    public class AvailableDeviceDto
    {
        public int Pk { get; set; }
        public string DeviceImei { get; set; } = string.Empty;
        public int BatteryLevel { get; set; }
        public string SignalStrength { get; set; } = "Good";
        public string BatteryStatus =>
            BatteryLevel >= 80 ? "High" :
            BatteryLevel >= 60 ? "Medium" : "Low";   // 51-59 = Low but still shown
        public DateTime LastSeen { get; set; }
    }

    // ── POST body for assignment ──────────────────────────────────────────────
    public class AssignDeviceRequest
    {
        public int TripPk { get; set; }
        public string DeviceImei { get; set; } = string.Empty;
        public string VehicleNo { get; set; } = string.Empty;
    }

    // ── Generic API envelope ──────────────────────────────────────────────────
    public class ApiResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
    }
}