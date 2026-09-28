using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Security.Claims;
using TrackingMVC.Data;

namespace TrackingMVC.Filters
{
    /// <summary>
    /// Runs globally (registered once in Program.cs) on every request. Stows
    /// the current user's full effective page set on ViewData["AllowedPages"]
    /// so _Layout.cshtml can decide which nav links to render without every
    /// controller wiring that up for itself. Anonymous requests (e.g. the
    /// login page) just leave it unset — _Layout treats that as "show nothing".
    /// </summary>
    public class PageNavContextFilter : IAsyncActionFilter
    {
        private readonly PagePermissionRepository _repo;
        public PageNavContextFilter(PagePermissionRepository repo) => _repo = repo;

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var user = context.HttpContext.User;
            if (user.Identity?.IsAuthenticated == true && context.Controller is Controller controller)
            {
                var role = user.FindFirstValue(ClaimTypes.Role) ?? "";
                if (int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                {
                    controller.ViewData["AllowedPages"] = await _repo.GetEffectivePagesAsync(userId, role);
                }
            }
            await next();
        }
    }
}
