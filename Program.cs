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
builder.Services.AddHostedService<ScheduledWhatsAppCampaignPublisher>();
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

        // ── One-time additive schema patch: G2M WhatsApp API credential fields ──
        // Connect now goes through G2M's own WhatsApp API layer, which needs
        // a Username/Password/User ID alongside the existing Phone Number ID
        // + Access Token fields. Password is stored Data-Protection-encrypted
        // (see WhatsAppController), never plaintext.
        foreach (var (col, ddl) in new[]
        {
            ("username", "ALTER TABLE `whatsapp_integrations` ADD COLUMN `username` VARCHAR(200) NULL"),
            ("password", "ALTER TABLE `whatsapp_integrations` ADD COLUMN `password` VARCHAR(1000) NULL"),
            ("user_id_field", "ALTER TABLE `whatsapp_integrations` ADD COLUMN `user_id_field` VARCHAR(200) NULL"),
            ("is_default", "ALTER TABLE `whatsapp_integrations` ADD COLUMN `is_default` TINYINT(1) NOT NULL DEFAULT 0"),
        })
        {
            using var checkWaColCmd = conn.CreateCommand();
            checkWaColCmd.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'whatsapp_integrations' AND COLUMN_NAME = @col";
            var p = checkWaColCmd.CreateParameter();
            p.ParameterName = "@col";
            p.Value = col;
            checkWaColCmd.Parameters.Add(p);
            var colExists = Convert.ToInt32(checkWaColCmd.ExecuteScalar()) > 0;

            if (!colExists)
            {
                using var alterWaColCmd = conn.CreateCommand();
                alterWaColCmd.CommandText = ddl;
                alterWaColCmd.ExecuteNonQuery();
            }
        }

        // A user's very first-ever connected number becomes their default —
        // without this backfill, everyone connected before IsDefault existed
        // would have zero default numbers and the dashboard couldn't pick one.
        // Both joined subqueries are derived tables (materialized before the
        // UPDATE runs) — MySQL refuses a correlated subquery directly against
        // the table being updated, so this can't be a plain WHERE NOT EXISTS.
        using var backfillDefaultCmd = conn.CreateCommand();
        backfillDefaultCmd.CommandText = @"
            UPDATE whatsapp_integrations wi
            JOIN (SELECT user_id, MIN(id) AS first_id FROM whatsapp_integrations WHERE is_active = 1 GROUP BY user_id) f
              ON wi.id = f.first_id
            LEFT JOIN (SELECT DISTINCT user_id FROM whatsapp_integrations WHERE is_default = 1) d
              ON d.user_id = wi.user_id
            SET wi.is_default = 1
            WHERE d.user_id IS NULL";
        backfillDefaultCmd.ExecuteNonQuery();

        // ── One-time additive schema patch: WhatsApp delivery-status tracking ──
        // PageReplies is the shared cross-platform outgoing-message table
        // (PascalCase columns, EF default convention — see the existing
        // Id/SourceId/etc. columns). Adding two nullable, WhatsApp-only
        // columns so an incoming "statuses" webhook event can be matched
        // back to the exact row it's reporting on (see WebhookController).
        foreach (var (col, ddl) in new[]
        {
            ("WaMessageId", "ALTER TABLE `PageReplies` ADD COLUMN `WaMessageId` VARCHAR(255) NULL"),
            ("DeliveryStatus", "ALTER TABLE `PageReplies` ADD COLUMN `DeliveryStatus` VARCHAR(20) NULL"),
        })
        {
            using var checkPrColCmd = conn.CreateCommand();
            checkPrColCmd.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'PageReplies' AND COLUMN_NAME = @col";
            var p2 = checkPrColCmd.CreateParameter();
            p2.ParameterName = "@col";
            p2.Value = col;
            checkPrColCmd.Parameters.Add(p2);
            var colExists2 = Convert.ToInt32(checkPrColCmd.ExecuteScalar()) > 0;

            if (!colExists2)
            {
                using var alterPrColCmd = conn.CreateCommand();
                alterPrColCmd.CommandText = ddl;
                alterPrColCmd.ExecuteNonQuery();
            }
        }

        // ── One-time additive schema patch: WhatsApp saved templates ─────
        using var createTemplatesCmd = conn.CreateCommand();
        createTemplatesCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `WhatsAppTemplates` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `UserId` INT NOT NULL,
                `Name` VARCHAR(200) NOT NULL,
                `LanguageCode` VARCHAR(20) NOT NULL DEFAULT 'en',
                `SampleParams` VARCHAR(1000) NULL,
                `Description` VARCHAR(500) NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_wat_user_id` (`UserId`)
            )";
        createTemplatesCmd.ExecuteNonQuery();

        // ── One-time additive schema patch: Campaign detail page fields ──
        // Phone number sent-from, scheduling, template category on
        // campaigns; per-recipient variable mapping on recipients; template
        // category on the saved-templates list. All additive/nullable.
        foreach (var (table, col, ddl) in new[]
        {
            ("WhatsAppCampaigns", "PhoneNumberId", "ALTER TABLE `WhatsAppCampaigns` ADD COLUMN `PhoneNumberId` VARCHAR(100) NULL"),
            ("WhatsAppCampaigns", "TemplateType", "ALTER TABLE `WhatsAppCampaigns` ADD COLUMN `TemplateType` VARCHAR(20) NULL"),
            ("WhatsAppCampaigns", "ScheduledAt", "ALTER TABLE `WhatsAppCampaigns` ADD COLUMN `ScheduledAt` DATETIME NULL"),
            ("WhatsAppCampaignRecipients", "VariableValues", "ALTER TABLE `WhatsAppCampaignRecipients` ADD COLUMN `VariableValues` VARCHAR(1000) NULL"),
            ("WhatsAppTemplates", "TemplateType", "ALTER TABLE `WhatsAppTemplates` ADD COLUMN `TemplateType` VARCHAR(20) NOT NULL DEFAULT 'UTILITY'"),
            ("WhatsAppTemplates", "DefinitionJson", "ALTER TABLE `WhatsAppTemplates` ADD COLUMN `DefinitionJson` TEXT NULL"),
            ("WhatsAppTemplates", "Status", "ALTER TABLE `WhatsAppTemplates` ADD COLUMN `Status` VARCHAR(20) NOT NULL DEFAULT 'pending'"),
            ("WhatsAppTemplates", "UsableAt", "ALTER TABLE `WhatsAppTemplates` ADD COLUMN `UsableAt` DATETIME NULL"),
        })
        {
            using var checkColCmd = conn.CreateCommand();
            checkColCmd.CommandText =
                "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @table AND COLUMN_NAME = @col";
            var pTable = checkColCmd.CreateParameter(); pTable.ParameterName = "@table"; pTable.Value = table;
            var pCol = checkColCmd.CreateParameter(); pCol.ParameterName = "@col"; pCol.Value = col;
            checkColCmd.Parameters.Add(pTable);
            checkColCmd.Parameters.Add(pCol);
            var colExists3 = Convert.ToInt32(checkColCmd.ExecuteScalar()) > 0;

            if (!colExists3)
            {
                using var alterColCmd = conn.CreateCommand();
                alterColCmd.CommandText = ddl;
                alterColCmd.ExecuteNonQuery();
            }
        }

        // ── One-time additive schema patch: LinkedIn expanded permissions ──
        // Multi-account support (IsDefault, mirrors WhatsApp's pattern) +
        // widening GrantedScopes (was VARCHAR(100) — too short once Lead
        // Sync/Events/Conversions scopes are added to the OAuth request) +
        // new tables for Events, Conversions, and Lead webhook subscriptions.
        using var checkLiDefaultCmd = conn.CreateCommand();
        checkLiDefaultCmd.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'linkedin_integrations' AND COLUMN_NAME = 'IsDefault'";
        var liDefaultExists = Convert.ToInt32(checkLiDefaultCmd.ExecuteScalar()) > 0;
        if (!liDefaultExists)
        {
            using var alterLiDefaultCmd = conn.CreateCommand();
            alterLiDefaultCmd.CommandText = "ALTER TABLE `linkedin_integrations` ADD COLUMN `IsDefault` TINYINT(1) NOT NULL DEFAULT 0";
            alterLiDefaultCmd.ExecuteNonQuery();
        }

        using var widenScopesCmd = conn.CreateCommand();
        widenScopesCmd.CommandText = "ALTER TABLE `linkedin_integrations` MODIFY COLUMN `GrantedScopes` VARCHAR(500) NULL";
        widenScopesCmd.ExecuteNonQuery();

        // A user's first-ever connected LinkedIn account becomes default —
        // same backfill pattern as WhatsApp's (see that block's comment for
        // why the LEFT JOIN form is required instead of a correlated
        // WHERE NOT EXISTS against the same table being updated).
        using var backfillLiDefaultCmd = conn.CreateCommand();
        backfillLiDefaultCmd.CommandText = @"
            UPDATE linkedin_integrations li
            JOIN (SELECT UserId, MIN(Id) AS first_id FROM linkedin_integrations WHERE IsActive = 1 GROUP BY UserId) f
              ON li.Id = f.first_id
            LEFT JOIN (SELECT DISTINCT UserId FROM linkedin_integrations WHERE IsDefault = 1) d
              ON d.UserId = li.UserId
            SET li.IsDefault = 1
            WHERE d.UserId IS NULL";
        backfillLiDefaultCmd.ExecuteNonQuery();

        // Track which connected LinkedIn account each post was made through —
        // without this, Post History mixes posts from every LinkedIn account
        // a panel user has ever connected, since they all share the same
        // panel UserId. Existing rows stay NULL (unattributable, pre-dates
        // multi-account support); new posts set it going forward.
        using var checkLiPostIntegCmd = conn.CreateCommand();
        checkLiPostIntegCmd.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.COLUMNS " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'linkedin_posts' AND COLUMN_NAME = 'LinkedInIntegrationId'";
        var liPostIntegExists = Convert.ToInt32(checkLiPostIntegCmd.ExecuteScalar()) > 0;
        if (!liPostIntegExists)
        {
            using var alterLiPostIntegCmd = conn.CreateCommand();
            alterLiPostIntegCmd.CommandText = "ALTER TABLE `linkedin_posts` ADD COLUMN `LinkedInIntegrationId` INT NULL";
            alterLiPostIntegCmd.ExecuteNonQuery();
        }

        using var createLiEventsCmd = conn.CreateCommand();
        createLiEventsCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `LinkedInEvents` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `UserId` VARCHAR(100) NOT NULL,
                `LinkedInIntegrationId` INT NOT NULL,
                `Name` VARCHAR(255) NOT NULL,
                `Description` TEXT NULL,
                `EventType` VARCHAR(30) NOT NULL,
                `OrganizerUrn` VARCHAR(150) NOT NULL,
                `StartsAt` BIGINT NOT NULL,
                `EndsAt` BIGINT NULL,
                `ExternalUrl` VARCHAR(500) NULL,
                `AddressJson` TEXT NULL,
                `LinkedInEventId` VARCHAR(100) NULL,
                `LiveVideoUrn` VARCHAR(150) NULL,
                `VanityName` VARCHAR(255) NULL,
                `UgcPostUrn` VARCHAR(150) NULL,
                `Status` VARCHAR(20) NOT NULL DEFAULT 'draft',
                `ErrorMessage` TEXT NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_lie_user_id` (`UserId`)
            )";
        createLiEventsCmd.ExecuteNonQuery();

        using var createLiConvRulesCmd = conn.CreateCommand();
        createLiConvRulesCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `LinkedInConversionRules` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `UserId` VARCHAR(100) NOT NULL,
                `LinkedInIntegrationId` INT NOT NULL,
                `Name` VARCHAR(255) NOT NULL,
                `AdAccountUrn` VARCHAR(150) NOT NULL,
                `ConversionType` VARCHAR(30) NOT NULL DEFAULT 'LEAD',
                `ConversionUrn` VARCHAR(150) NULL,
                `Enabled` TINYINT(1) NOT NULL DEFAULT 1,
                `Status` VARCHAR(20) NOT NULL DEFAULT 'draft',
                `ErrorMessage` TEXT NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_licr_user_id` (`UserId`)
            )";
        createLiConvRulesCmd.ExecuteNonQuery();

        using var createLiConvEventsCmd = conn.CreateCommand();
        createLiConvEventsCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `LinkedInConversionEvents` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `ConversionRuleId` INT NOT NULL,
                `EventId` VARCHAR(100) NOT NULL,
                `Amount` DECIMAL(12,2) NULL,
                `CurrencyCode` VARCHAR(10) NULL,
                `UserIdentifierType` VARCHAR(50) NOT NULL,
                `Success` TINYINT(1) NOT NULL,
                `ErrorMessage` TEXT NULL,
                `SentAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_lice_rule_id` (`ConversionRuleId`)
            )";
        createLiConvEventsCmd.ExecuteNonQuery();

        using var createLiLeadSubCmd = conn.CreateCommand();
        createLiLeadSubCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `LinkedInLeadSubscriptions` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `UserId` VARCHAR(100) NOT NULL,
                `LinkedInIntegrationId` INT NOT NULL,
                `OwnerUrn` VARCHAR(150) NOT NULL,
                `WebhookUrl` VARCHAR(500) NOT NULL,
                `LinkedInSubscriptionId` VARCHAR(100) NULL,
                `Active` TINYINT(1) NOT NULL DEFAULT 1,
                `Status` VARCHAR(20) NOT NULL DEFAULT 'pending',
                `ErrorMessage` TEXT NULL,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                KEY `idx_lils_user_id` (`UserId`)
            )";
        createLiLeadSubCmd.ExecuteNonQuery();

        // ── One-time additive schema patch: WhatsApp bulk campaigns ──────
        // New tables (not altering anything existing) — one row per
        // campaign, one row per recipient so partial-failure sends still
        // show exactly who succeeded/failed and why.
        using var createCampaignCmd = conn.CreateCommand();
        createCampaignCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `WhatsAppCampaigns` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `UserId` INT NOT NULL,
                `Name` VARCHAR(200) NOT NULL,
                `MessageType` VARCHAR(20) NOT NULL DEFAULT 'template',
                `TemplateName` VARCHAR(200) NULL,
                `LanguageCode` VARCHAR(20) NULL,
                `TemplateParams` VARCHAR(1000) NULL,
                `MessageText` TEXT NULL,
                `MediaUrl` VARCHAR(1000) NULL,
                `Caption` VARCHAR(1000) NULL,
                `FileName` VARCHAR(255) NULL,
                `Status` VARCHAR(20) NOT NULL DEFAULT 'pending',
                `TotalRecipients` INT NOT NULL DEFAULT 0,
                `SentCount` INT NOT NULL DEFAULT 0,
                `FailedCount` INT NOT NULL DEFAULT 0,
                `CreatedAt` DATETIME NOT NULL DEFAULT CURRENT_TIMESTAMP,
                `CompletedAt` DATETIME NULL,
                KEY `idx_wac_user_id` (`UserId`)
            )";
        createCampaignCmd.ExecuteNonQuery();

        using var createCampaignRecipientCmd = conn.CreateCommand();
        createCampaignRecipientCmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS `WhatsAppCampaignRecipients` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `CampaignId` INT NOT NULL,
                `PhoneNumber` VARCHAR(30) NOT NULL,
                `Status` VARCHAR(20) NOT NULL DEFAULT 'pending',
                `MessageId` VARCHAR(255) NULL,
                `ErrorMessage` VARCHAR(1000) NULL,
                `SentAt` DATETIME NULL,
                KEY `idx_wacr_campaign_id` (`CampaignId`)
            )";
        createCampaignRecipientCmd.ExecuteNonQuery();

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