using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    [RequireLogin]
    public class SummaryController : Controller
    {
        private readonly DbHelper _db;
        public SummaryController(DbHelper db) => _db = db;

        public IActionResult Index()
        {
            var vm = new SummaryViewModel();
            try
            {
                using var con = _db.GetConnection();
                con.Open();

                using (var cmd = new SqlCommand("SELECT COUNT(*) FROM [atmparking].[dbo].[gps_devices_vta]", con))
                    vm.TotalDevices = (int)cmd.ExecuteScalar()!;

                using (var cmd = new SqlCommand(
                    "SELECT COUNT(*) FROM [atmparking].[dbo].[gps_devices_vta] WHERE [last_seen] >= DATEADD(MINUTE,-30,GETDATE())", con))
                    vm.OnlineDevices = (int)cmd.ExecuteScalar()!;

                vm.OfflineDevices = vm.TotalDevices - vm.OnlineDevices;

                try
                {
                    using var cmd = new SqlCommand("SELECT COUNT(*) FROM [atmparking].[dbo].[login_users]", con);
                    vm.TotalUsers = (int)cmd.ExecuteScalar()!;
                }
                catch { }

                const string devSql = @"SELECT TOP 5 [id],[imei],[last_seen]
                                        FROM [atmparking].[dbo].[gps_devices_vta]
                                        ORDER BY [last_seen] DESC";
                using (var cmd = new SqlCommand(devSql, con))
                using (var dr = cmd.ExecuteReader())
                {
                    while (dr.Read())
                    {
                        DateTime? ls = dr["last_seen"] == DBNull.Value ? null : Convert.ToDateTime(dr["last_seen"]);
                        bool online  = ls.HasValue && (DateTime.Now - ls.Value).TotalMinutes <= 30;
                        vm.RecentDevices.Add(new DeviceAsset
                        {
                            Id       = Convert.ToInt32(dr["id"]),
                            Imei     = dr["imei"].ToString()!,
                            LastSeen = ls.HasValue ? ls.Value.ToString("dd MMM yyyy HH:mm") : "Never",
                            Status   = online ? "online" : "offline"
                        });
                    }
                }
            }
            catch (Exception ex) { ViewBag.DbError = ex.Message; }

            vm.RecentLogs = new List<LogEntry>
            {
                new() { Id="LG001", Timestamp=DateTime.Now.AddDays(-1),   Type="INFO",    User="admin",    Source="DeviceService",  Message="Daily device health check passed" },
                new() { Id="LG002", Timestamp=DateTime.Now.AddDays(-2),   Type="WARNING", User="system",   Source="TrackingJob",    Message="Some trackers did not respond" },
                new() { Id="LG003", Timestamp=DateTime.Now.AddDays(-3),   Type="ERROR",   User="admin",    Source="BillingService", Message="Invoice generation failed" },
                new() { Id="LG004", Timestamp=DateTime.Now.AddDays(-5),   Type="INFO",    User="operator", Source="Login",          Message="User operator logged in" },
                new() { Id="LG005", Timestamp=DateTime.Now.AddMonths(-1), Type="ERROR",   User="system",   Source="API Gateway",    Message="Third-party API timeout" },
            };

            return View(vm);
        }
    }
}
