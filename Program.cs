using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MySql.EntityFrameworkCore.Extensions;
using SocialMediaPanel.Data;
using SocialMediaPanel.Services;
using SocialMediaPanel.Hubs;

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
builder.Services.AddScoped<WhatsAppBusinessService>();
builder.Services.AddScoped<ActivePageService>();
builder.Services.AddScoped<InsightsSyncService>();
builder.Services.AddScoped<FacebookAdsService>();
builder.Services.AddScoped<PostPublishingService>();
builder.Services.AddHostedService<ScheduledPostPublisher>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<AuditLogService>();

// Standalone WhatsApp-PDF relay feature (Drive link -> public PDF URL -> WhatsApp send API)
builder.Services.AddHttpClient<GoogleDrivePdfService>();
builder.Services.AddHttpClient<WhatsAppSendService>();

// Standalone WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf) — Excel + a plain/flat
// sample PDF (a real notice with real example values already on it, no form fields) ->
// one edited PDF per row. Own login, own tables, own everything; unrelated to the relay
// feature above beyond both being "WhatsApp + PDF".
builder.Services.AddScoped<SocialMediaPanel.Services.PdfMerge.ExcelParsingService>();
builder.Services.AddScoped<SocialMediaPanel.Services.PdfMerge.PdfTextLocatorService>();
builder.Services.AddScoped<SocialMediaPanel.Services.PdfMerge.PdfTextReplaceService>();
builder.Services.AddScoped<SocialMediaPanel.Services.PdfMerge.PdfMergeAuthService>();
builder.Services.AddScoped<SocialMediaPanel.Services.PdfMerge.PdfMergeBatchService>();

// PdfSharp 6.x needs an explicit font resolver (no more GDI/system-font dependency —
// which matters for the Linux deploy server anyway); see PdfMergeFontResolver.cs.
PdfSharp.Fonts.GlobalFontSettings.FontResolver = new SocialMediaPanel.Services.PdfMerge.PdfMergeFontResolver(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Fonts"));

// Swagger — scoped to ONLY the WhatsApp-PDF relay controller (this app is otherwise an
// MVC app with session-cookie auth; exposing every controller's routes as a public API
// doc would leak internal surface area for no reason).
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("whatsapp-pdf-relay", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "WhatsApp PDF Relay API",
        Version = "v1",
        Description = "Google Drive PDF -> public URL -> WhatsApp send-message relay. Standalone feature, separate from the rest of the panel."
    });
    c.DocInclusionPredicate((docName, apiDesc) =>
        apiDesc.ActionDescriptor is Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor cad
        && cad.ControllerName == "WhatsAppPdf");
});

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
builder.Services.AddSignalR();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

//app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseSwagger(c => c.RouteTemplate = "swagger/{documentName}/swagger.json");
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/whatsapp-pdf-relay/swagger.json", "WhatsApp PDF Relay API");
    c.RoutePrefix = "swagger";
});

app.UseRouting();
app.UseSession();
app.UseAuthorization();

// MVC Route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.MapControllers();
app.MapHub<InboxHub>("/hubs/inbox");

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

        // ── One-time additive schema patch: WhatsApp Business API (WABA) ──
        // Real Meta Cloud API integration (Views/WhatsApp, WhatsAppController,
        // WhatsAppBusinessService) — distinct from the standalone WhatsApp-PDF
        // Mail-Merge feature below. No OAuth consent screen exists for this
        // product, so credentials are entered once per user via the page's own
        // UI and stored here, same idempotent CREATE-IF-NOT-EXISTS approach.
        using var createWaCmd = conn.CreateCommand();
        createWaCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `whatsapp_integrations` (
                `id` INT AUTO_INCREMENT PRIMARY KEY,
                `user_id` INT NOT NULL,
                `phone_number_id` VARCHAR(100) NOT NULL,
                `waba_id` VARCHAR(100) NULL,
                `access_token` TEXT NOT NULL,
                `display_phone_number` VARCHAR(30) NULL,
                `verified_name` VARCHAR(200) NULL,
                `is_active` TINYINT(1) NOT NULL DEFAULT 1,
                `connected_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_wa_user_id` (`user_id`),
                KEY `idx_wa_phone_number_id` (`phone_number_id`)
            )";
        createWaCmd.ExecuteNonQuery();

        // ── One-time additive schema patch: WhatsApp-PDF Mail-Merge tables ──
        // Brand-new standalone feature (Areas/WhatsAppPdf) — own login, own batch/
        // record tracking. Same idempotent CREATE-IF-NOT-EXISTS approach as the
        // ALTER TABLE patches above, for the same reason (no Migrations/ folder,
        // live shared DB).
        using var createUsersCmd = conn.CreateCommand();
        createUsersCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `pdfmerge_users` (
                `id` INT AUTO_INCREMENT PRIMARY KEY,
                `username` VARCHAR(100) NOT NULL UNIQUE,
                `password_hash` VARCHAR(255) NOT NULL,
                `role` VARCHAR(20) NOT NULL DEFAULT 'user',
                `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP
            )";
        createUsersCmd.ExecuteNonQuery();

        using var createBatchesCmd = conn.CreateCommand();
        createBatchesCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `pdfmerge_batches` (
                `id` INT AUTO_INCREMENT PRIMARY KEY,
                `user_id` INT NOT NULL,
                `excel_file_name` VARCHAR(255) NOT NULL,
                `sample_pdf_file_name` VARCHAR(255) NOT NULL,
                `phone_column` VARCHAR(100) NULL,
                `total_records` INT NOT NULL DEFAULT 0,
                `status` VARCHAR(20) NOT NULL DEFAULT 'completed',
                `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (`user_id`) REFERENCES `pdfmerge_users`(`id`)
            )";
        createBatchesCmd.ExecuteNonQuery();

        using var createRecordsCmd = conn.CreateCommand();
        createRecordsCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `pdfmerge_records` (
                `id` INT AUTO_INCREMENT PRIMARY KEY,
                `batch_id` INT NOT NULL,
                `row_index` INT NOT NULL,
                `data_json` TEXT NOT NULL,
                `phone_number` VARCHAR(30) NULL,
                `generated_file_name` VARCHAR(255) NOT NULL,
                `generated_url` VARCHAR(500) NOT NULL,
                `whatsapp_status` VARCHAR(20) NULL,
                `whatsapp_message_id` VARCHAR(255) NULL,
                `created_at` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (`batch_id`) REFERENCES `pdfmerge_batches`(`id`)
            )";
        createRecordsCmd.ExecuteNonQuery();
    }
    finally
    {
        conn.Close();
    }
}

app.Run();