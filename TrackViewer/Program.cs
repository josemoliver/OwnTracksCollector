using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using TrackViewer;
using TrackViewer.Models;
using TrackViewer.Services;

// ── CLI helper: dotnet run -- --hash-password <plain> ──────────────────────
if (args is ["--hash-password", var plain])
{
    Console.WriteLine(BCrypt.Net.BCrypt.HashPassword(plain));
    return;
}

var builder = WebApplication.CreateBuilder(args);

// ── WAL mode for safe concurrent read + write ────────────────────────────────
var dbPath = builder.Configuration["Database:Path"]
    ?? throw new InvalidOperationException("Database:Path must be set in appsettings.json or TRACKVIEWER_Database__Path env var.");

if (File.Exists(dbPath))
{
    DbInitialiser.EnableWal(dbPath);
    using var startupLog = LoggerFactory.Create(l => l.AddConsole());
    DbInitialiser.EnsureIndexes(dbPath, startupLog.CreateLogger("DbInitialiser"));
}

// ── Authentication ────────────────────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath   = "/login";
        o.LogoutPath  = "/account/logout";
        o.AccessDeniedPath = "/login";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(
            builder.Configuration.GetValue("Auth:CookieExpiryMinutes", 480));
        o.Cookie.HttpOnly    = true;
        o.Cookie.SameSite    = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.SlidingExpiration  = true;
    });
builder.Services.AddAuthorization();

// ── Rate limiter (login brute-force protection) ───────────────────────────────
builder.Services.AddRateLimiter(o =>
    o.AddFixedWindowLimiter("login", w =>
    {
        w.Window           = TimeSpan.FromMinutes(1);
        w.PermitLimit      = 5;
        w.QueueLimit       = 0;
        w.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    }));

// ── Blazor ────────────────────────────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();

// ── Application services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<LocationQueryService>();
builder.Services.AddSingleton<WaypointQueryService>();
builder.Services.AddSingleton<ReportQueryService>();
builder.Services.AddSingleton<GpxExportService>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

// ── Pipeline ──────────────────────────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── Auth endpoints ────────────────────────────────────────────────────────────
app.MapPost("/account/login", async (HttpContext ctx, IConfiguration config, ILogger<Program> log) =>
{
    var form     = await ctx.Request.ReadFormAsync();
    var username = form["username"].ToString();
    var password = form["password"].ToString();
    var returnUrl = form["returnUrl"].ToString();

    var cfgUser = config["Auth:Username"] ?? string.Empty;
    var cfgHash = config["Auth:PasswordHash"] ?? string.Empty;

    if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password) ||
        !string.Equals(username, cfgUser, StringComparison.Ordinal) ||
        !BCrypt.Net.BCrypt.Verify(password, cfgHash))
    {
        log.LogWarning("Failed login attempt for user '{User}' from {IP}",
            username, ctx.Connection.RemoteIpAddress);
        ctx.Response.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl)}");
        return;
    }

    var claims = new[] { new Claim(ClaimTypes.Name, username) };
    var identity   = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal  = new ClaimsPrincipal(identity);

    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal,
        new AuthenticationProperties { IsPersistent = true });

    var safe = string.IsNullOrEmpty(returnUrl) || !Uri.IsWellFormedUriString(returnUrl, UriKind.Relative)
        ? "/"
        : returnUrl;
    ctx.Response.Redirect(safe);
}).RequireRateLimiting("login");

app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    ctx.Response.Redirect("/login");
});

// ── GPX export endpoint ───────────────────────────────────────────────────────
app.MapPost("/api/export/gpx", async (
    HttpContext           ctx,
    LocationQueryService  lqs,
    WaypointQueryService  wqs,
    GpxExportService      gpx,
    ILogger<Program>      log) =>
{
    var req = await ctx.Request.ReadFromJsonAsync<ExportRequest>();
    if (req is null) return Results.BadRequest("Invalid request body.");

    var filter = new TrackFilter
    {
        SelectedDevices = req.Devices,
        From            = req.From,
        To              = req.To,
    };

    var (locations, _) = await lqs.GetLocationsAsync(filter);
    var allWaypoints   = await wqs.GetWaypointsAsync(req.Devices);
    var waypoints      = allWaypoints.Where(w => req.WaypointIds.Contains(w.Id)).ToList();

    log.LogInformation("GPX export: {Points} locations, {Wpts} waypoints for user {User}",
        locations.Count, waypoints.Count, ctx.User.Identity?.Name);

    var stream = await gpx.ExportAsync(req, locations, waypoints);
    return Results.File(stream, "application/gpx+xml", "owntracks-export.gpx");
}).RequireAuthorization();

// ── Blazor ────────────────────────────────────────────────────────────────────
app.MapRazorPages();
app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();

