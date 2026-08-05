namespace TrackingMVC.Models
{
    public class LoginViewModel
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public bool RememberMe { get; set; }
        public string? Error { get; set; }
    }

    public class DashboardViewModel
    {
        public int TotalDevices { get; set; }
        public int OnlineDevices { get; set; }
        public int OfflineDevices { get; set; }
        public List<DeviceAsset> Devices { get; set; } = new();
        public List<GeoFenceLocation> GeoFences { get; set; } = new();
        public List<ParkingPoint> ParkingPoints { get; set; } = new();
    }

    public class DeviceAsset
    {
        public int Id { get; set; }
        public string Imei { get; set; } = "";
        public string Name { get; set; } = "";
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int Geofence { get; set; }
        public int GeoFenceId { get; set; }   // FK ? GeoFenceLocation.Id
        public string LastSeen { get; set; } = "Unknown";
        public string Status { get; set; } = "offline";
    }

    public class GeoFenceLocation
    {
        public int Id { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Name { get; set; } = "";
        public int RadiusMeters { get; set; }
    }

    public class ParkingPoint
    {
        public int Id { get; set; }
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public int ParkingLocationId { get; set; }
        public string CreatedAt { get; set; } = "";
    }

    public class TrackViewModel
    {
        public string? Imei { get; set; }
        public string? DateFrom { get; set; }
        public string? DateTo { get; set; }
        public List<TrackPoint> Points { get; set; } = new();
        public string? Error { get; set; }
    }

    public class TrackPoint
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
        public string Timestamp { get; set; } = "";
        public double? Speed { get; set; }
    }

    public class SummaryViewModel
    {
        public int TotalDevices { get; set; }
        public int OnlineDevices { get; set; }
        public int OfflineDevices { get; set; }
        public int TotalUsers { get; set; }
        public List<DeviceAsset> RecentDevices { get; set; } = new();
        public List<LogEntry> RecentLogs { get; set; } = new();
    }

    public class AdminViewModel
    {
        public List<UserRecord> Users { get; set; } = new();
        public string? Message { get; set; }
        public bool IsError { get; set; }
    }

    public class UserRecord
    {
        public int Id { get; set; }
        public string Username { get; set; } = "";
        public string Email { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "";
        public bool IsActive { get; set; }
        public DateTime? LastLogin { get; set; }
    }

    public class LogEntry
    {
        public string Id { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "";
        public string User { get; set; } = "";
        public string Source { get; set; } = "";
        public string Message { get; set; } = "";
    }
}