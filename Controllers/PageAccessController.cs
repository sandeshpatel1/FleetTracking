using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using TrackingMVC.Data;
using TrackingMVC.Models;

namespace TrackingMVC.Controllers
{
    // Deliberately [Authorize(Roles = "admin")] rather than
    // [RequirePageAccess] — granting page access is the one thing that stays
    // exclusive to a real admin, even though a non-admin could theoretically
    // be granted the "Admin Panel" page itself.
    [Authorize(Roles = "admin")]
    public class PageAccessController : Controller
    {
        private readonly PagePermissionRepository _repo;
        public PageAccessController(PagePermissionRepository repo) => _repo = repo;

        public async Task<IActionResult> Index(string? msg)
        {
            ViewData["Title"] = "Page Access";
            ViewBag.Active = "pageaccess";
            ViewBag.Msg = msg;
            ViewBag.Pages = PageAccess.AllPageKeys;
            ViewBag.Labels = PageAccess.Labels;
            var users = await _repo.GetAllUsersWithAccessAsync();
            return View(users);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Save(int userId, List<string>? allowedPages)
        {
            var updatedBy = int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var uid) ? uid : 0;
            var allowed = new HashSet<string>(allowedPages ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            await _repo.SaveUserAccessAsync(userId, allowed, updatedBy);
            return RedirectToAction("Index", new { msg = "Access updated." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetToDefault(int userId)
        {
            await _repo.ResetUserToRoleDefaultAsync(userId);
            return RedirectToAction("Index", new { msg = "Reset to role defaults." });
        }
    }
}
