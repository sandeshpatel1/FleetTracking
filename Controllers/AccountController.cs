using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TrackingMVC.Data;
using TrackingMVC.Models;
using TrackingMVC.Services;

namespace TrackingMVC.Controllers
{
    public class AccountController : Controller
    {
        private readonly DbHelper _db;
        private readonly JwtTokenService _jwt;
        private readonly IConfiguration _cfg;

        public AccountController(DbHelper db, JwtTokenService jwt, IConfiguration cfg)
        {
            _db = db;
            _jwt = jwt;
            _cfg = cfg;
        }

        [HttpGet]
        public IActionResult Login(string? returnUrl = null)
        {
            if (User.Identity?.IsAuthenticated == true)
                return RedirectToAction("Index", "Home");

            var model = new LoginViewModel { ReturnUrl = returnUrl };
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

            var (ok, error, authUser) = ValidateCredentials(model.Username, model.Password);
            if (!ok || authUser == null)
            {
                model.Error = error ?? "Invalid username or password.";
                return View(model);
            }

            IssueTokenCookie(authUser);
            UpdateLastLogin(authUser.UserId);

            if (model.RememberMe)
                Response.Cookies.Append("RememberUser", model.Username,
                    new CookieOptions { Expires = DateTimeOffset.Now.AddDays(30), HttpOnly = true });
            else
                Response.Cookies.Delete("RememberUser");

            if (!string.IsNullOrEmpty(model.ReturnUrl) && Url.IsLocalUrl(model.ReturnUrl))
                return Redirect(model.ReturnUrl);

            return RedirectToAction("Index", "Home");
        }

        // POST /Account/LoginApi — same credential check as the form login,
        // but returns the JWT in the JSON body instead of a cookie, for a
        // non-browser client (mobile app, external script) that will send it
        // back itself as "Authorization: Bearer <token>" on later calls.
        [HttpPost]
        public IActionResult LoginApi([FromBody] LoginViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model?.Username) || string.IsNullOrWhiteSpace(model?.Password))
                return Json(new { ok = false, message = "Username and password are required." });

            var (ok, error, authUser) = ValidateCredentials(model.Username, model.Password);
            if (!ok || authUser == null)
                return Json(new { ok = false, message = error ?? "Invalid username or password." });

            var token = _jwt.GenerateToken(authUser.UserId, authUser.Username, authUser.FullName, authUser.Role, authUser.Email);
            UpdateLastLogin(authUser.UserId);

            return Json(new
            {
                ok = true,
                token,
                tokenType = "Bearer",
                username = authUser.Username,
                role = authUser.Role,
                expiresInMinutes = int.TryParse(_cfg["Jwt:ExpiryMinutes"], out var m) ? m : 480
            });
        }

        public IActionResult Logout()
        {
            Response.Cookies.Delete("access_token");
            Response.Cookies.Delete("RememberUser");
            return RedirectToAction("Login");
        }

        public IActionResult Error() => View();

        public IActionResult AccessDenied() => View();

        // ── Shared credential check used by both Login and LoginApi ────────
        private (bool Ok, string? Error, AuthUser? User) ValidateCredentials(string username, string password)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();

                // NOTE: password is still compared as plaintext here, same as
                // before this change — not something this pass touched, but
                // worth moving to a proper hash (PBKDF2/BCrypt) separately.
                const string sql = @"
                    SELECT [id],[username],[email],[full_name],[role],[is_active]
                    FROM   [atmparking].[dbo].[login_users]
                    WHERE  ([username] = @u OR [email] = @u)
                      AND  [password]  = @p";

                using var cmd = new SqlCommand(sql, con);
                cmd.Parameters.AddWithValue("@u", username.Trim());
                cmd.Parameters.AddWithValue("@p", password);

                using var dr = cmd.ExecuteReader();
                if (!dr.Read())
                    return (false, "Invalid username or password.", null);

                if (!Convert.ToBoolean(dr["is_active"]))
                    return (false, "Your account is disabled. Contact the administrator.", null);

                return (true, null, new AuthUser
                {
                    UserId = Convert.ToInt32(dr["id"]),
                    Username = dr["username"].ToString()!,
                    FullName = dr["full_name"] == DBNull.Value ? dr["username"].ToString()! : dr["full_name"].ToString()!,
                    Role = dr["role"].ToString()!,
                    Email = dr["email"].ToString()!
                });
            }
            catch (SqlException ex)
            {
                return (false, $"Database error (SQL {ex.Number}): {ex.Message}", null);
            }
            catch (Exception ex)
            {
                return (false, "Connection error: " + ex.Message, null);
            }
        }

        private void IssueTokenCookie(AuthUser u)
        {
            var token = _jwt.GenerateToken(u.UserId, u.Username, u.FullName, u.Role, u.Email);
            var expiryMinutes = int.TryParse(_cfg["Jwt:ExpiryMinutes"], out var m) ? m : 480;

            Response.Cookies.Append("access_token", token, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes)
            });
        }

        private void UpdateLastLogin(int userId)
        {
            try
            {
                using var con = _db.GetConnection();
                con.Open();
                using var cmd = new SqlCommand(
                    "UPDATE [atmparking].[dbo].[login_users] SET [last_login]=GETDATE() WHERE [id]=@id", con);
                cmd.Parameters.AddWithValue("@id", userId);
                cmd.ExecuteNonQuery();
            }
            catch { /* non-critical */ }
        }

        private class AuthUser
        {
            public int UserId { get; set; }
            public string Username { get; set; } = "";
            public string FullName { get; set; } = "";
            public string Role { get; set; } = "";
            public string Email { get; set; } = "";
        }
    }
}
