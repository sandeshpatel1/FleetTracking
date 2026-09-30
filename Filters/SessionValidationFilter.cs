using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;
using TrackingMVC.Data;

namespace TrackingMVC.Filters
{
    /// <summary>
    /// Runs globally, after authentication. Confirms the JWT's "sid" claim
    /// still matches the session recorded in login_users — if another login
    /// has since taken over this account, this browser is signed out and
    /// sent back to the login page instead of continuing to browse under a
    /// dead session. Also refreshes the session heartbeat so
    /// SessionRepository's inactivity timeout works correctly.
    ///
    /// A token issued before this feature existed has no "sid" claim — that
    /// case is let through rather than rejected, so people aren't force
    /// logged out the moment this ships; it naturally stops mattering once
    /// that old token expires.
    /// </summary>
    public class SessionValidationFilter : IAsyncActionFilter
    {
        private readonly SessionRepository _sessions;
        public SessionValidationFilter(SessionRepository sessions) => _sessions = sessions;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated == true)
            {
                var sidStr = user.FindFirstValue("sid");
                var uidStr = user.FindFirstValue(ClaimTypes.NameIdentifier);

                if (Guid.TryParse(sidStr, out var sid) && int.TryParse(uidStr, out var uid))
                {
                    var stillValid = await _sessions.TouchAsync(uid, sid);
                    if (!stillValid)
                    {
                        context.HttpContext.Response.Cookies.Delete("access_token");
                        context.Result = new RedirectToActionResult("Login", "Account",
                            new { msg = "You were signed out because this account was used to log in elsewhere." });
                        return;
                    }
                }
            }
            await next();
        }
    }
}