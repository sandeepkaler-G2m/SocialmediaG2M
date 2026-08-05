using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MySql.EntityFrameworkCore.Extensions;
using SocialMediaPanel.Data;
using SocialMediaPanel.Services;

var builder = WebApplication.CreateBuilder(args);

var listenPort = int.TryParse(Environment.GetEnvironmentVariable("APP_PORT"), out var envPort) ? envPort : 8080;
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(listenPort);
    options.Limits.MaxRequestBodySize = 104857600;
});

builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(104857600)); // ? ADD: 100MB
});

builder.Services.AddControllersWithViews();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(
        new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "DataProtectionKeys")));

builder.Services.AddHttpClient();
builder.Services.AddScoped<FacebookService>();
builder.Services.AddScoped<InstagramService>();
builder.Services.AddScoped<NativeInstagramService>();
builder.Services.AddScoped<PostService>();
builder.Services.AddScoped<LinkedInService>();
builder.Services.AddScoped<ActivePageService>();
builder.Services.AddScoped<InsightsSyncService>();
builder.Services.AddScoped<FacebookAdsService>();
builder.Services.AddScoped<PostPublishingService>();
builder.Services.AddHostedService<ScheduledPostPublisher>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<AuditLogService>();

builder.Services.AddHttpClient<SocialMediaPanel.Services.GmailService>(c =>
    c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<SocialMediaPanel.Services.GmailIntegrationService>();

// MySQL
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseMySQL(
        builder.Configuration.GetConnectionString("db")
    )
);

// Session
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IPostService, PostService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

//app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

// MVC Route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.MapControllers();

// Auto Migrate
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.Migrate();

    // ── One-time additive schema patch: Post Scheduling ──────────────────
    // This repo has no EF Core migrations even though it runs against a
    // live shared MySQL DB (see project memory: project-no-ef-migrations),
    // so a real `dotnet ef migrations add` isn't safe here — it would try
    // to recreate every existing table. This idempotent check+ALTER is the
    // hand-rolled stand-in: it only ever adds one nullable column, checked
    // first so it's a no-op after the first run (safe to leave in permanently,
    // same as db.Database.Migrate() above).
    var conn = db.Database.GetDbConnection();
    conn.Open();
    try
    {
        using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'SocialPosts' AND COLUMN_NAME = 'scheduled_at'";
        var exists = Convert.ToInt32(checkCmd.ExecuteScalar()) > 0;

        if (!exists)
        {
            using var alterCmd = conn.CreateCommand();
            alterCmd.CommandText = "ALTER TABLE `SocialPosts` ADD COLUMN `scheduled_at` DATETIME NULL";
            alterCmd.ExecuteNonQuery();
        }

        // ── One-time additive schema patch: native Instagram Login token ──
        // Accounts connected via the direct "Instagram API with Instagram
        // Login" flow carry their own access token (no linked Facebook Page
        // to resolve one from), unlike the existing Facebook-linked rows.
        using var checkCmd2 = conn.CreateCommand();
        checkCmd2.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'InstagramAccounts' AND COLUMN_NAME = 'NativeAccessToken'";
        var existsIg = Convert.ToInt32(checkCmd2.ExecuteScalar()) > 0;

        if (!existsIg)
        {
            using var alterCmd2 = conn.CreateCommand();
            alterCmd2.CommandText = "ALTER TABLE `InstagramAccounts` ADD COLUMN `NativeAccessToken` VARCHAR(512) NULL";
            alterCmd2.ExecuteNonQuery();
        }
    }
    finally
    {
        conn.Close();
    }
}

app.Run();