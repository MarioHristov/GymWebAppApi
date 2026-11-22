using GymWebApi.Clubhouse;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using System.Security.Claims;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
if (configuration["DataProtectionKeyPath"] is string keyPath)
    builder.Services.AddDataProtection().PersistKeysToFileSystem(Directory.CreateDirectory(keyPath));
var sql = configuration.GetConnectionString("GymWebApiContextConnection");
var dataDir = configuration["WEBSITE_SITE_NAME"] != null && configuration["HOME"] is string azureHome ? Directory.CreateDirectory(Path.Combine(azureHome, "data", "clubhouse")).FullName : builder.Environment.ContentRootPath;
var sqlite = configuration.GetConnectionString("ClubSqlite") ?? new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
{
    DataSource = Path.Combine(dataDir, "clubhouse.db"),
    DefaultTimeout = 10
}.ToString();
void ConfigureDatabase(DbContextOptionsBuilder options)
{
    if (string.IsNullOrWhiteSpace(sql))
        options.UseSqlite(sqlite);
#if SQL_SERVER
    else
        options.UseSqlServer(sql);
#else
    else throw new InvalidOperationException("Build with the SQL Server provider for this connection.");
#endif
}

builder.Services.AddDbContext<AccountDb>(ConfigureDatabase);
builder.Services.AddDbContext<ClubDb>(ConfigureDatabase);
builder.Services.AddSingleton(Catalog.Load(builder.Environment.ContentRootPath));
builder.Services.AddSingleton<IPasswordHasher<IdentityUser>, PasswordHasher<IdentityUser>>();
builder.Services.AddAuthentication("member").AddCookie("member", options =>
{
    options.Cookie.Name = "shredded.member";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnRedirectToLogin = c =>
    {
        c.Response.StatusCode = 401;
        return Task.CompletedTask;
    };
    options.Events.OnRedirectToAccessDenied = c =>
    {
        c.Response.StatusCode = 403;
        return Task.CompletedTask;
    };
    options.Events.OnValidatePrincipal = async c =>
    {
        var id = c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var account = await c.HttpContext.RequestServices.GetRequiredService<AccountDb>().Users.AsNoTracking().Where(x => x.Id == id).Select(x => new { x.SecurityStamp }).FirstOrDefaultAsync();
        if (account == null || account.SecurityStamp != c.Principal?.FindFirstValue("stamp"))
            c.RejectPrincipal();
    };
});
builder.Services.AddAuthorization();
var origins = configuration.GetSection("AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.AddPolicy("accounts", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "local", _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(2), QueueLimit = 0 }));
});
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    await Storage.Initialize(scope.ServiceProvider.GetRequiredService<AccountDb>(), "AspNetUsers");
    await Storage.Initialize(scope.ServiceProvider.GetRequiredService<ClubDb>(), "GymMemberProfiles");
}

app.UseCors();
app.UseRateLimiter();
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/UserProfile"))
    {
        ctx.Response.Headers.CacheControl = "no-store";
        // A custom header requires a successful CORS preflight for cross-origin browser mutations.
        if (ctx.Request.Method != "GET" && ctx.Request.Method != "HEAD" && ctx.Request.Method != "OPTIONS" && ctx.Request.Headers["X-Gym-Request"] != "1" && !(ctx.Request.Path.StartsWithSegments("/UserProfile") && ctx.Request.HasJsonContentType()))
        {
            ctx.Response.StatusCode = 400;
            return;
        }
    }

    try
    {
        await next();
    }
    catch (DbUpdateException)when (!ctx.Response.HasStarted)
    {
        await Results.Conflict(new { error = "The data changed. Refresh and try again." }).ExecuteAsync(ctx);
    }
});
app.UseAuthentication();
app.UseAuthorization();
Accounts.Map(app);
Gym.Map(app);
if (configuration["FrontendRoot"] is string root)
{
    var files = new PhysicalFileProvider(Path.GetFullPath(root));
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
}

app.Run();
