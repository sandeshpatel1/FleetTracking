using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using TrackingMVC.Data;
using TrackingMVC.Filters;
using TrackingMVC.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<DbHelper>();
builder.Services.AddScoped<DeviceAssignmentRepository>();
builder.Services.AddScoped<PagePermissionRepository>();
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddHostedService<TripActivityWorker>();

// Global filter: computes the logged-in user's effective page-access set once
// per request and stashes it on ViewData for _Layout.cshtml to read (nav
// link visibility). See Filters/PageNavContextFilter.cs.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<PageNavContextFilter>();
});

// ── Authentication: JWT is now the ONLY auth mechanism (session-based login
// is gone). The token is issued on login as an HttpOnly cookie for normal
// page navigation, and the exact same token can be sent back as
// "Authorization: Bearer <token>" by an API/mobile client — OnMessageReceived
// below is what lets one scheme serve both.
var jwtSection = builder.Configuration.GetSection("Jwt");
var jwtKey = jwtSection["SecretKey"]
    ?? throw new InvalidOperationException("Jwt:SecretKey is missing from appsettings.json");

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = jwtSection["Issuer"],
        ValidAudience = jwtSection["Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };

    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (string.IsNullOrEmpty(ctx.Request.Headers.Authorization) &&
                ctx.Request.Cookies.TryGetValue("access_token", out var cookieToken))
            {
                ctx.Token = cookieToken;
            }
            return Task.CompletedTask;
        },
        // A browser hitting a protected page with no/expired token gets sent
        // to the login page instead of a bare 401 JSON body. A genuine API
        // caller (sent an Authorization header, or explicitly wants JSON)
        // still gets a plain 401.
        OnChallenge = ctx =>
        {
            var sentBearer = ctx.Request.Headers.Authorization.Count > 0;
            var acceptsHtml = ctx.Request.Headers.Accept.ToString().Contains("text/html");
            if (!sentBearer && acceptsHtml)
            {
                ctx.HandleResponse();
                var returnUrl = Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString);
                ctx.Response.Redirect($"/Account/Login?returnUrl={returnUrl}");
            }
            return Task.CompletedTask;
        },
        OnForbidden = ctx =>
        {
            if (ctx.Request.Headers.Accept.ToString().Contains("text/html"))
            {
                ctx.Response.Redirect("/Account/AccessDenied");
                return Task.CompletedTask;
            }
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    };
});

builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Account/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();
