using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TrackingMVC.Filters
{
    public class RequireLoginAttribute : ActionFilterAttribute
    {
        public override void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.HttpContext.Session.GetInt32("UserID") == null)
            {
                context.Result = new RedirectToActionResult("Login", "Account", null);
                return;
            }
            base.OnActionExecuting(context);
        }
    }
}
