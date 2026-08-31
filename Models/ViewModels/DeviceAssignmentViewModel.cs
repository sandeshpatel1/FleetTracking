namespace TrackingMVC.Models.ViewModels
{
    // ── Autocomplete suggestion ────────────────────────────────────────────────
    public class AutocompleteItem
    {
        public int Pk { get; set; }
        public string Display { get; set; } = "";   // what shows in the dropdown
        public string VehicleNo { get; set; } = "";
        public string TripId { get; set; } = "";
        public int AssignedFlag { get; set; }
        public string StatusLabel => AssignedFlag == 1 ? "Assigned" : "Pending";
    }

    // ── Full trip detail row ───────────────────────────────────────────────────
    public class TripDetailDto
    {
        public int Pk { get; set; }
        public string TripId { get; set; } = "";
        public string VehicleNo { get; set; } = "";
        public string SerialNo { get; set; } = "";
        public string DriverNo { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public int AssignedFlag { get; set; }
        public int Battery { get; set; }          // from gps_trip_detail.battery (testing)
    }

    // ── Last known GPS position ────────────────────────────────────────────────
    public class LastLocationDto
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public double Speed { get; set; }
        public int Battery { get; set; }
        public DateTime RecordedAt { get; set; }
    }

    // ── Available unassigned device (battery > 50 %) ──────────────────────────
    public class AvailableDeviceDto
    {
        public int Pk { get; set; }
        public string DeviceImei { get; set; } = "";
        public int BatteryLevel { get; set; }
        public DateTime LastSeen { get; set; }
        public string BatteryStatus =>
            BatteryLevel >= 80 ? "High" :
            BatteryLevel >= 60 ? "Medium" : "Low";
    }

    // ── POST body for assignment ───────────────────────────────────────────────
    public class AssignDeviceRequest
    {
        public int TripPk { get; set; }
        public string DeviceImei { get; set; } = "";
        public string VehicleNo { get; set; } = "";
    }

    // ── Standard API envelope ─────────────────────────────────────────────────
    public class ApiResponse
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = "";
    }
}