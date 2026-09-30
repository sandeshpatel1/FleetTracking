using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    [Authorize]
    public class AdminController : Controller
    {
        private readonly DbHelper _db;
        private readonly ILogger<AdminController> _log;

        public AdminController(DbHelper db, ILogger<AdminController> log)
        {
            _db = db;
            _log = log;
        }

        [RequirePageAccess(PageAccess.AdminPanel)]
        public IActionResult Index()
        {
            var vm = new AdminViewModel
            {
                Message = TempData["Msg"] as string,
                IsError = TempData["MsgErr"] as string == "1"
            };
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"SELECT [id],[username],[email],[full_name],[role],[is_active],[last_login]
                                     FROM [atmparking].[dbo].[login_users]
                                     ORDER BY [id]";
                using var cmd = new SqlCommand(sql, con);
                using var dr = cmd.ExecuteReader();
                while (dr.Read())
                    vm.Users.Add(new UserRecord
                    {
                        Id = Convert.ToInt32(dr["id"]),
                        Username = dr["username"].ToString()!,
                        Email = dr["email"].ToString()!,
                        FullName = dr["full_name"] == DBNull.Value ? "" : dr["full_name"].ToString()!,
                        Role = dr["role"].ToString()!,
                        IsActive = Convert.ToBoolean(dr["is_active"]),
                        LastLogin = dr["last_login"] == DBNull.Value ? null : Convert.ToDateTime(dr["last_login"])
                    });
            }
            catch (Exception ex)
            {
                var code = NewRef();
                _log.LogError(ex, "Admin list users failed. Ref {Ref}", code);
                ViewBag.DbError = $"Could not load users. (Ref: {code})";
            }
            return View(vm);
        }

        [RequirePageAccess(PageAccess.AdminPanel)]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ToggleUser(int id)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                using var cmd = new SqlCommand(
                    "UPDATE [atmparking].[dbo].[login_users] SET [is_active]=CASE WHEN [is_active]=1 THEN 0 ELSE 1 END WHERE [id]=@id", con);
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
                return Notify("User status updated.", false);
            }
            catch (Exception ex)
            {
                var code = NewRef();
                _log.LogError(ex, "ToggleUser failed for id {Id}. Ref {Ref}", id, code);
                return Notify($"Failed to update user status. (Ref: {code})", true);
            }
        }

        [RequirePageAccess(PageAccess.AdminPanel)]
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddUser(string username, string email, string? fullName, string role, string password)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"INSERT INTO [atmparking].[dbo].[login_users]
                                     ([username],[email],[full_name],[role],[password],[is_active],[created_at])
                                     VALUES(@u,@e,@fn,@r,@p,1,GETDATE())";
                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@e", email);
                cmd.Parameters.AddWithValue("@fn", fullName ?? "");
                cmd.Parameters.AddWithValue("@r", role);
                cmd.Parameters.AddWithValue("@p", password);
                cmd.ExecuteNonQuery();
                return Notify($"User '{username}' created successfully.", false);
            }
            catch (SqlException ex) when (ex.Number is 2627 or 2601)
            {
                // Duplicate username/email: safe and useful to tell the user.
                _log.LogWarning(ex, "AddUser duplicate for {Username}", username);
                return Notify("Failed to create user. That username or email already exists.", true);
            }
            catch (Exception ex)
            {
                var code = NewRef();
                _log.LogError(ex, "AddUser failed for {Username}. Ref {Ref}", username, code);
                return Notify($"Failed to create user. Please contact the developer. (Ref: {code})", true);
            }
        }

        private IActionResult Notify(string msg, bool isError)
        {
            TempData["Msg"] = msg;
            TempData["MsgErr"] = isError ? "1" : "0";
            return RedirectToAction("Index");
        }

        private static string NewRef() => Guid.NewGuid().ToString("N")[..8].ToUpper();
    }
}