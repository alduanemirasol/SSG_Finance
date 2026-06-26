using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MyMvcApp.Data;
using MyMvcApp.Services;

LoadDotEnv();

var builder = WebApplication.CreateBuilder(args);

var configuredPort = builder.Configuration["PORT"];
if (!string.IsNullOrWhiteSpace(configuredPort) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://+:{configuredPort}");
}

// Add services to the container.
// AutoValidateAntiforgeryToken enforces CSRF validation on ALL unsafe HTTP methods
// (POST/PUT/DELETE/PATCH) across every controller — GET/HEAD/OPTIONS are exempt.
builder.Services.AddControllersWithViews(options =>
    {
        options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    })
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

// CSRF (anti-forgery) configuration.
// HeaderName matches what the frontend fetch() calls already send.
// SameAsRequest keeps the cookie working on the school's HTTP LAN while staying Secure over HTTPS.
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "RequestVerificationToken";
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

// Add Entity Framework Core
var connectionString = BuildDatabaseConnectionString(builder.Configuration)
    ?? builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Database connection is not configured. Set DB_HOST, DB_PORT, DB_DATABASE, DB_USERNAME, and DB_PASSWORD in .env.");
}

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

// Add Authentication Service
builder.Services.AddScoped<IAuthService, AuthService>();

// Add Email Service
builder.Services.Configure<SmtpSettings>(options =>
{
    var smtpSettings = builder.Configuration.GetSection("SmtpSettings");
    options.Host = GetConfigValue(builder.Configuration["SMTP_HOST"], smtpSettings["Host"]);
    options.UserName = GetConfigValue(builder.Configuration["SMTP_USERNAME"], smtpSettings["UserName"]);
    options.Password = GetConfigValue(builder.Configuration["SMTP_PASSWORD"], smtpSettings["Password"]);

    if (int.TryParse(GetConfigValue(builder.Configuration["SMTP_PORT"], smtpSettings["Port"], "587"), out var smtpPort))
    {
        options.Port = smtpPort;
    }

    options.EnableSsl = !bool.TryParse(smtpSettings["EnableSsl"], out var enableSsl) || enableSsl;
});
builder.Services.AddScoped<IEmailService, EmailService>();

// Add SSE Service
builder.Services.AddSingleton<SseService>();

// Add Session services
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;

    // Fix: in local/dev (often HTTP), SecurePolicy=Always prevents the session cookie from being stored.
    // Use None to allow cookie over HTTP; browser will still send it over HTTPS normally.
    options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.None;

    // Fix: Strict SameSite can block cookies on some navigation / fetch flows.
    // Lax allows cookies for top-level navigation.
    options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Lax;
});

var app = builder.Build();

// Seed the database
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await ApplicationDbContextSeed.SeedAsync(context);
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
else
{
    // In development, use a custom error page to hide file details
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "text/html";
            await context.Response.WriteAsync(@"
                <!DOCTYPE html>
                <html>
                <head><title>Error</title></head>
                <body>
                    <h1>An error occurred</h1>
                    <p>Please try again later or contact support.</p>
                </body>
                </html>");
        });
    });
}

// Enforce HTTPS even on LAN.
// Use HSTS for all environments (self-signed HTTPS is supported on school LAN).
app.UseHsts();


// Add security headers to hide server information
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.XXSSProtection = "1; mode=block";
    context.Response.Headers.Server = string.Empty;

    // Prevent browsers from caching HTML pages (especially authenticated dashboards).
    // Without this, the Back/Forward button can serve a cached dashboard from the
    // browser's history/bfcache after logout, exposing data without hitting the server.
    // Scoped to text/html so static assets (CSS/JS/images) keep their normal caching.
    context.Response.OnStarting(() =>
    {
        var contentType = context.Response.ContentType;
        if (!string.IsNullOrEmpty(contentType) &&
            contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
        }
        return Task.CompletedTask;
    });

    await next();
});

if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
app.UseRouting();

app.UseSession();
app.UseAuthorization();

// Issue a JS-readable anti-forgery request token for HTML page loads.
// The shared /js/csrf.js reads this cookie and echoes it back in the
// RequestVerificationToken header on every mutating fetch(), which the
// AutoValidateAntiforgeryToken filter validates server-side.
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    bool isStatic =
        path.StartsWith("/css", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/js", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/lib", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/images", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/uploads", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase);

    if (HttpMethods.IsGet(context.Request.Method) && !isStatic)
    {
        var antiforgery = context.RequestServices.GetRequiredService<IAntiforgery>();
        var tokens = antiforgery.GetAndStoreTokens(context);
        context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // must be readable by JavaScript
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true
        });
    }

    await next();
});

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static void LoadDotEnv()
{
    var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
    if (!File.Exists(envPath))
    {
        return;
    }

    foreach (var rawLine in File.ReadAllLines(envPath))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
        {
            continue;
        }

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
        {
            continue;
        }

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();

        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        if (Environment.GetEnvironmentVariable(key) is null)
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

static string? BuildDatabaseConnectionString(ConfigurationManager configuration)
{
    var dbValues = new Dictionary<string, string>
    {
        ["DB_HOST"] = configuration["DB_HOST"] ?? string.Empty,
        ["DB_PORT"] = configuration["DB_PORT"] ?? string.Empty,
        ["DB_DATABASE"] = configuration["DB_DATABASE"] ?? string.Empty,
        ["DB_USERNAME"] = configuration["DB_USERNAME"] ?? string.Empty,
        ["DB_PASSWORD"] = configuration["DB_PASSWORD"] ?? string.Empty
    };

    if (dbValues.Values.All(string.IsNullOrWhiteSpace))
    {
        return null;
    }

    var missingKeys = dbValues
        .Where(pair => string.IsNullOrWhiteSpace(pair.Value))
        .Select(pair => pair.Key)
        .ToArray();

    if (missingKeys.Length > 0)
    {
        throw new InvalidOperationException($"Database environment is incomplete. Missing: {string.Join(", ", missingKeys)}.");
    }

    return $"server={dbValues["DB_HOST"]};port={dbValues["DB_PORT"]};database={dbValues["DB_DATABASE"]};uid={dbValues["DB_USERNAME"]};pwd={dbValues["DB_PASSWORD"]};";
}

static string GetConfigValue(params string?[] values)
{
    return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
