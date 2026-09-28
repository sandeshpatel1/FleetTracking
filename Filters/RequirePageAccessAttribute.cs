using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;
using TrackingMVC.Data;

namespace TrackingMVC.Filters
{
    /// <summary>
    /// Gate an action behind one or more page keys (see PageAccess). Pass more
    /// than one when an endpoint legitimately serves more than one page (e.g.
    /// DashboardMapJson feeds both the Dashboard and the Live Tracking map) —
    /// having access to ANY of them is enough.
    ///
    /// This must sit alongside [Authorize] (which confirms the user IS
    /// someone); this attribute only decides whether that user may open THIS
    /// page. Admins bypass it entirely — see PagePermissionRepository.
    /// </summary>
    public class RequirePageAccessAttribute : ActionFilterAttribute
    {
        private readonly string[] _pageKeys;
        public RequirePageAccessAttribute(params string[] pageKeys) => _pageKeys = pageKeys;

        public override async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var user = context.HttpContext.User;
            var role = user.FindFirstValue(ClaimTypes.Role) ?? "";

            if (!string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase))
            {
                var userIdStr = user.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!int.TryParse(userIdStr, out var userId))
                {
                    context.Result = new RedirectToActionResult("Login", "Account", null);
                    return;
                }

                var repo = context.HttpContext.RequestServices.GetRequiredService<PagePermissionRepository>();
                var allowed = false;
                foreach (var key in _pageKeys)
                {
                    if (await repo.HasAccessAsync(userId, role, key)) { allowed = true; break; }
                }

                if (!allowed)
                {
                    context.Result = new RedirectToActionResult("AccessDenied", "Account", null);
                    return;
                }
            }

            await next();
        }
    }
}
