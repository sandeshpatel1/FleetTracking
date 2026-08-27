using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    [RequireLogin]
    public class AdminController : Controller
    {
        private readonly DbHelper _db;
        public AdminController(DbHelper db) => _db = db;

        public IActionResult Index(string? msg, bool isError = false)
        {
            var vm = new AdminViewModel { Message = msg, IsError = isError };
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"SELECT [id],[username],[email],[full_name],[role],[is_active],[last_login]
                                     FROM [atmparking].[dbo].[login_users]
                                     ORDER BY [id]";
                using var cmd = new SqlCommand(sql, con);
                using var dr  = cmd.ExecuteReader();
                while (dr.Read())
                    vm.Users.Add(new UserRecord
                    {
                        Id        = Convert.ToInt32(dr["id"]),
                        Username  = dr["username"].ToString()!,
                        Email     = dr["email"].ToString()!,
                        FullName  = dr["full_name"] == DBNull.Value ? "" : dr["full_name"].ToString()!,
                        Role      = dr["role"].ToString()!,
                        IsActive  = Convert.ToBoolean(dr["is_active"]),
                        LastLogin = dr["last_login"] == DBNull.Value ? null : Convert.ToDateTime(dr["last_login"])
                    });
            }
            catch (Exception ex) { ViewBag.DbError = ex.Message; }
            return View(vm);
        }

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
                return RedirectToAction("Index", new { msg = "User status updated successfully.", isError = false });
            }
            catch (Exception ex)
            {
                return RedirectToAction("Index", new { msg = "Error: " + ex.Message, isError = true });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult AddUser(string username, string email, string fullName, string role, string password)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                const string sql = @"INSERT INTO [atmparking].[dbo].[login_users]
                                     ([username],[email],[full_name],[role],[password],[is_active])
                                     VALUES(@u,@e,@fn,@r,@p,1)";
                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@u",  username);
                cmd.Parameters.AddWithValue("@e",  email);
                cmd.Parameters.AddWithValue("@fn", fullName);
                cmd.Parameters.AddWithValue("@r",  role);
                cmd.Parameters.AddWithValue("@p",  password);
                cmd.ExecuteNonQuery();
                return RedirectToAction("Index", new { msg = $"User '{username}' created successfully.", isError = false });
            }
            catch (Exception ex)
            {
                return RedirectToAction("Index", new { msg = "Error adding user: " + ex.Message, isError = true });
            }
        }
    }
}
