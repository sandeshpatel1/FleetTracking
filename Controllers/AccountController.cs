using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    public class AccountController : Controller
    {
        private readonly DbHelper _db;

        public AccountController(DbHelper db) => _db = db;

        [HttpGet]
        public IActionResult Login()
        {
            if (HttpContext.Session.GetInt32("UserID") != null)
                return RedirectToAction("Index", "Home");

            var model = new LoginViewModel();
            if (Request.Cookies.TryGetValue("RememberUser", out var savedUser))
            {
                model.Username = savedUser;
                model.RememberMe = true;
            }
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(LoginViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Username) || string.IsNullOrWhiteSpace(model.Password))
            {
                model.Error = "Please enter both username and password.";
                return View(model);
            }

            try
            {
                using var con = _db.GetConnection();
                con.Open();

                const string sql = @"
                    SELECT [id],[username],[email],[full_name],[role],[is_active]
                    FROM   [Sunmoon_Enterprises].[sa_lio].[login_users]
                    WHERE  ([username] = @u OR [email] = @u)
                      AND  [password]  = @p";

                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@u", model.Username.Trim());
                cmd.Parameters.AddWithValue("@p", model.Password);

                using var dr = cmd.ExecuteReader();
                if (dr.Read())
                {
                    if (!Convert.ToBoolean(dr["is_active"]))
                    {
                        model.Error = "Your account is disabled. Contact the administrator.";
                        return View(model);
                    }

                    int userId   = Convert.ToInt32(dr["id"]);
                    string uname = dr["username"].ToString()!;
                    string fname = dr["full_name"] == DBNull.Value ? uname : dr["full_name"].ToString()!;
                    string role  = dr["role"].ToString()!;
                    string email = dr["email"].ToString()!;

                    HttpContext.Session.SetInt32("UserID",   userId);
                    HttpContext.Session.SetString("UserName", fname);
                    HttpContext.Session.SetString("UserRole", role);
                    HttpContext.Session.SetString("UserEmail", email);

                    dr.Close();
                    UpdateLastLogin(userId);

                    if (model.RememberMe)
                        Response.Cookies.Append("RememberUser", model.Username,
                            new CookieOptions { Expires = DateTimeOffset.Now.AddDays(30), HttpOnly = true });
                    else
                        Response.Cookies.Delete("RememberUser");

                    return RedirectToAction("Index", "Home");
                }

                model.Error = "Invalid username or password.";
            }
            catch (SqlException ex)
            {
                model.Error = $"Database error (SQL {ex.Number}): {ex.Message}";
            }
            catch (Exception ex)
            {
                model.Error = "Connection error: " + ex.Message;
            }

            return View(model);
        }

        public IActionResult Logout()
        {
            HttpContext.Session.Clear();
            Response.Cookies.Delete("RememberUser");
            return RedirectToAction("Login");
        }

        public IActionResult Error() => View();

        private void UpdateLastLogin(int userId)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                using var cmd = new SqlCommand(
                    "UPDATE [Sunmoon_Enterprises].[sa_lio].[login_users] SET [last_login]=GETDATE() WHERE [id]=@id", con);
                cmd.Parameters.AddWithValue("@id", userId);
                cmd.ExecuteNonQuery();
            }
            catch { /* non-critical */ }
        }
    }
}
