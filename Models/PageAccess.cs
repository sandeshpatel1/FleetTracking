namespace TrackingMVC.Models
{
    /// <summary>
    /// The fixed set of "pages" (nav items) access can be granted for, plus
    /// each non-admin role's default set. Admins are deliberately absent from
    /// RoleDefaults — the "admin" role always bypasses this whole system (see
    /// PagePermissionRepository.HasAccessAsync), it never needs a default list.
    ///
    /// Per-user overrides on top of these defaults live in the DB
    /// (dbo.user_page_access), not here — this class only holds the fixed
    /// shape of the system (page keys, labels) and the role baseline.
    /// </summary>
    public static class PageAccess
    {
        public const string Dashboard        = "dashboard";
        public const string LiveTracking     = "map";
        public const string TrackHistory     = "trackplay";
        public const string DeviceAssignment = "deviceassignment";
        public const string Summary          = "summary";
        public const string AdminPanel       = "admin";

        public static readonly string[] AllPageKeys =
        {
            Dashboard, LiveTracking, TrackHistory, DeviceAssignment, Summary, AdminPanel
        };

        public static readonly Dictionary<string, string> Labels = new()
        {
            [Dashboard]        = "Dashboard",
            [LiveTracking]     = "Live Tracking",
            [TrackHistory]     = "Track History",
            [DeviceAssignment] = "Device Assignment",
            [Summary]          = "Summary",
            [AdminPanel]       = "Admin Panel"
        };

        // Only non-admin roles need a default — "admin" always has everything.
        public static readonly Dictionary<string, string[]> RoleDefaults = new(StringComparer.OrdinalIgnoreCase)
        {
            ["operator"] = new[] { Dashboard, LiveTracking, TrackHistory, DeviceAssignment, Summary },
            ["viewer"]   = new[] { Dashboard, LiveTracking, TrackHistory }
        };
    }

    /// <summary>One row of the Page Access admin grid — a user plus their effective per-page access.</summary>
    public class UserPageAccessRow
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string FullName { get; set; } = "";
        public string Role { get; set; } = "";

        /// <summary>Explicit override rows for this user, keyed by page key.</summary>
        public Dictionary<string, bool> Overrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Role default merged with overrides — what the grid should actually show checked.</summary>
        public Dictionary<string, bool> EffectivePages { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
