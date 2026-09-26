using ClosedXML.Excel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Hubs;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;
using System.Text;
using System.Text.RegularExpressions;

namespace SocialMediaPanel.Controllers
{
    /// <summary>
    /// Bulk WhatsApp broadcast — send one template/text/media message to
    /// many numbers via G2M's WhatsApp send API (WhatsAppBusinessService).
    /// Lives under the WhatsApp section's own sub-nav (/WhatsApp/Campaigns),
    /// not a top-level nav tab.
    ///
    /// Recipients come from either a manually-typed comma-separated list, or
    /// an uploaded CSV/XLSX whose columns can be mapped to per-recipient
    /// template variables (see UploadRecipients + Create). "Send Now" runs
    /// immediately in the background; "Schedule" hands off to
    /// ScheduledWhatsAppCampaignPublisher (a BackgroundService, same pattern
    /// as ScheduledPostPublisher for the Posts feature).
    ///
    /// Every campaign send also writes a normal PageReply row (see
    /// RunCampaignAsync), so campaign sends count toward the WhatsApp
    /// dashboard's Total Outgoing/Delivered stats exactly like Inbox
    /// replies do, and this controller's own Delivered/Undelivered columns
    /// are computed from those same rows' DeliveryStatus — not guessed.
    /// </summary>
    [Route("WhatsApp/Campaigns")]
    public class WhatsAppCampaignController : Controller
    {
        private readonly AppDbContext _db;
        private readonly WhatsAppBusinessService _wa;
        private readonly IHubContext<InboxHub> _hub;
        private readonly IServiceScopeFactory _scopeFactory;

        public WhatsAppCampaignController(AppDbContext db, WhatsAppBusinessService wa, IHubContext<InboxHub> hub, IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _wa = wa;
            _hub = hub;
            _scopeFactory = scopeFactory;
        }

        private int? UserId => HttpContext.Session.GetInt32("UserId");

        // ══════════════════════════════════════════════════════════════
        // CAMPAIGN LIST
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("")]
        public async Task<IActionResult> Index()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.WhatsAppIntegrations
                .Where(w => w.UserId == UserId && w.IsActive)
                .OrderByDescending(w => w.IsDefault)
                .ToListAsync();
            ViewBag.Connected = integrations.Count > 0;
            ViewBag.Integrations = integrations;

            var campaigns = await _db.WhatsAppCampaigns
                .Where(c => c.UserId == UserId)
                .OrderByDescending(c => c.CreatedAt)
                .ToListAsync();

            var campaignIds = campaigns.Select(c => c.Id).ToList();
            var deliveredByCampaign = await _db.PageReplies
                .Where(r => r.SourceType == "campaign" && campaignIds.Contains(r.SourceId) && (r.DeliveryStatus == "delivered" || r.DeliveryStatus == "read"))
                .GroupBy(r => r.SourceId)
                .Select(g => new { CampaignId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.CampaignId, x => x.Count);

            var rows = campaigns.Select(c => new CampaignListRow
            {
                Campaign = c,
                Delivered = deliveredByCampaign.TryGetValue(c.Id, out var d) ? d : 0,
                Undelivered = c.SentCount - (deliveredByCampaign.TryGetValue(c.Id, out var d2) ? d2 : 0) + c.FailedCount
            }).ToList();

            return View(rows);
        }

        // ══════════════════════════════════════════════════════════════
        // CREATE CAMPAIGN — new page
        // ══════════════════════════════════════════════════════════════
        [HttpGet]
        [Route("Create")]
        public async Task<IActionResult> CreatePage()
        {
            if (UserId == null) return RedirectToAction("Login", "Account");

            var integrations = await _db.WhatsAppIntegrations.Where(w => w.UserId == UserId && w.IsActive).OrderByDescending(w => w.IsDefault).ToListAsync();
            if (integrations.Count == 0)
            {
                TempData["WhatsAppCampaignError"] = "Connect WhatsApp first (WhatsApp page) before creating a campaign.";
                return RedirectToAction("Index");
            }

            ViewBag.Integrations = integrations;
            ViewBag.Templates = await _db.WhatsAppTemplates.Where(t => t.UserId == UserId).OrderBy(t => t.Name).ToListAsync();
            return View("Create");
        }

        // Accepts a CSV or XLSX upload, returns its headers + every row as
        // JSON so the Create page can build the phone/variable mapping UI
        // and hold the full recipient set client-side (no server-side temp
        // storage needed — the final Create POST resends what it needs).
        [HttpPost]
        [Route("UploadRecipients")]
        public IActionResult UploadRecipients(IFormFile file)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });
            if (file == null || file.Length == 0) return Json(new { success = false, message = "No file uploaded." });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            try
            {
                List<string> headers;
                List<List<string>> rows;

                if (ext == ".csv")
                {
                    (headers, rows) = ParseCsv(file);
                }
                else if (ext == ".xlsx")
                {
                    (headers, rows) = ParseXlsx(file);
                }
                else if (ext == ".xls")
                {
                    return Json(new { success = false, message = "Old .xls format isn't supported — please save as .xlsx or .csv and re-upload." });
                }
                else
                {
                    return Json(new { success = false, message = "Unsupported file type — upload a .csv or .xlsx file." });
                }

                if (headers.Count == 0 || rows.Count == 0)
                    return Json(new { success = false, message = "The file has no data rows." });

                return Json(new { success = true, headers, rows, rowCount = rows.Count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Could not read the file: " + ex.Message });
            }
        }

        private static (List<string> headers, List<List<string>> rows) ParseCsv(IFormFile file)
        {
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8);
            var lines = new List<List<string>>();
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                lines.Add(SplitCsvLine(line));
            }
            if (lines.Count == 0) return (new(), new());

            var headers = lines[0];
            var rows = lines.Skip(1).ToList();
            return (headers, rows);
        }

        // Minimal CSV field splitter — handles double-quoted fields with
        // embedded commas ("Smith, John" style), which a plain .Split(',')
        // would break on.
        private static List<string> SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                var ch = line[i];
                if (inQuotes)
                {
                    if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else if (ch == '"') inQuotes = false;
                    else current.Append(ch);
                }
                else
                {
                    if (ch == '"') inQuotes = true;
                    else if (ch == ',') { fields.Add(current.ToString().Trim()); current.Clear(); }
                    else current.Append(ch);
                }
            }
            fields.Add(current.ToString().Trim());
            return fields;
        }

        private static (List<string> headers, List<List<string>> rows) ParseXlsx(IFormFile file)
        {
            using var stream = file.OpenReadStream();
            using var workbook = new XLWorkbook(stream);
            var sheet = workbook.Worksheets.First();
            var usedRange = sheet.RangeUsed();
            if (usedRange == null) return (new(), new());

            var allRows = usedRange.RowsUsed().ToList();
            if (allRows.Count == 0) return (new(), new());

            var headers = allRows[0].Cells().Select(c => c.GetString().Trim()).ToList();
            var rows = allRows.Skip(1)
                .Select(r => headers.Select((_, i) => r.Cell(i + 1).GetString().Trim()).ToList())
                .ToList();

            return (headers, rows);
        }

        // ══════════════════════════════════════════════════════════════
        // CREATE CAMPAIGN — submit (JSON body: template, recipients w/ per-row variables)
        // ══════════════════════════════════════════════════════════════
        [HttpPost]
        [Route("Create")]
        public async Task<IActionResult> Create([FromBody] CreateCampaignRequest req)
        {
            if (UserId == null) return Json(new { success = false, message = "Not logged in" });

            var integration = req.IntegrationId.HasValue
                ? await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.Id == req.IntegrationId && w.UserId == UserId && w.IsActive)
                : await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive && w.IsDefault)
                  ?? await _db.WhatsAppIntegrations.FirstOrDefaultAsync(w => w.UserId == UserId && w.IsActive);

            if (integration == null) return Json(new { success = false, message = "WhatsApp not connected." });
            if (string.IsNullOrWhiteSpace(req.Name)) return Json(new { success = false, message = "Campaign name is required." });
            if (string.IsNullOrWhiteSpace(req.TemplateName)) return Json(new { success = false, message = "Select a template." });

            var numbers = (req.Recipients ?? new())
                .Select(r => new { Phone = Regex.Replace(r.PhoneNumber ?? "", @"[^\d]", ""), r.Variables })
                .Where(r => r.Phone.Length >= 8)
                .GroupBy(r => r.Phone).Select(g => g.First()) // de-dupe by phone
                .ToList();

            if (numbers.Count == 0) return Json(new { success = false, message = "No valid phone numbers found." });

            var scheduledAt = req.ScheduledAt?.ToUniversalTime();
            var isScheduled = scheduledAt.HasValue && scheduledAt.Value > DateTime.UtcNow;

            var campaign = new WhatsAppCampaign
            {
                UserId = UserId!.Value,
                Name = req.Name.Trim(),
                MessageType = "template",
                TemplateName = req.TemplateName.Trim(),
                LanguageCode = string.IsNullOrWhiteSpace(req.LanguageCode) ? "en" : req.LanguageCode.Trim(),
                TemplateType = req.TemplateType,
                PhoneNumberId = integration.DisplayPhoneNumber ?? integration.PhoneNumberId,
                Status = isScheduled ? "scheduled" : "running",
                ScheduledAt = isScheduled ? scheduledAt : null,
                TotalRecipients = numbers.Count,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppCampaigns.Add(campaign);
            await _db.SaveChangesAsync();

            foreach (var n in numbers)
            {
                _db.WhatsAppCampaignRecipients.Add(new WhatsAppCampaignRecipient
                {
                    CampaignId = campaign.Id,
                    PhoneNumber = n.Phone,
                    Status = "pending",
                    VariableValues = (n.Variables != null && n.Variables.Count > 0) ? string.Join(",", n.Variables) : null
                });
            }
            await _db.SaveChangesAsync();

            if (!isScheduled)
            {
                var campaignId = campaign.Id;
                var apiKey = integration.AccessToken;
                var phoneNumberId = integration.PhoneNumberId;
                _ = Task.Run(async () => await RunCampaignAsync(campaignId, apiKey, phoneNumberId, _scopeFactory));
            }

            var msg = isScheduled
                ? $"Campaign \"{campaign.Name}\" scheduled for {scheduledAt:dd MMM yyyy, hh:mm tt} UTC — {numbers.Count} recipient(s)."
                : $"Campaign \"{campaign.Name}\" started — sending to {numbers.Count} number(s).";
            return Json(new { success = true, message = msg, campaignId = campaign.Id });
        }

        // Runs the actual send loop — called immediately for "Send Now"
        // campaigns, or by ScheduledWhatsAppCampaignPublisher once a
        // scheduled campaign's time arrives. Public+static so that hosted
        // service can call it without duplicating this logic.
        public static async Task RunCampaignAsync(int campaignId, string apiKey, string phoneNumberId, IServiceScopeFactory scopeFactory)
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var wa = scope.ServiceProvider.GetRequiredService<WhatsAppBusinessService>();
            var hub = scope.ServiceProvider.GetRequiredService<IHubContext<InboxHub>>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<WhatsAppCampaignController>>();

            var campaign = await db.WhatsAppCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId);
            if (campaign == null) return;

            campaign.Status = "running";
            await db.SaveChangesAsync();

            var recipients = await db.WhatsAppCampaignRecipients
                .Where(r => r.CampaignId == campaignId && r.Status == "pending")
                .ToListAsync();

            foreach (var recipient in recipients)
            {
                bool success; string? messageId; string error;
                var paramsList = string.IsNullOrWhiteSpace(recipient.VariableValues)
                    ? null
                    : recipient.VariableValues.Split(',').ToList();

                try
                {
                    (success, messageId, error) = await wa.SendG2MTemplateAsync(
                        apiKey, recipient.PhoneNumber, campaign.TemplateName!, campaign.LanguageCode ?? "en", paramsList);
                }
                catch (Exception ex)
                {
                    success = false; messageId = null; error = ex.Message;
                    logger.LogError("Campaign {CampaignId} send error for {Phone}: {Msg}", campaignId, recipient.PhoneNumber, ex.Message);
                }

                recipient.Status = success ? "sent" : "failed";
                recipient.MessageId = messageId;
                recipient.ErrorMessage = success ? null : error;
                recipient.SentAt = DateTime.UtcNow;

                if (success) campaign.SentCount++; else campaign.FailedCount++;

                db.PageReplies.Add(new PageReply
                {
                    SourceId = campaignId,
                    SourceType = "campaign",
                    Platform = "whatsapp",
                    PageId = recipient.PhoneNumber,
                    PostId = "",
                    ReplyText = $"[template: {campaign.TemplateName}]" + (paramsList != null ? " (" + string.Join(", ", paramsList) + ")" : ""),
                    SentByPageId = phoneNumberId,
                    IsSuccess = success,
                    ErrorMessage = success ? null : error,
                    WaMessageId = messageId,
                    DeliveryStatus = success ? "sent" : null,
                    CreatedAt = DateTime.UtcNow
                });

                await db.SaveChangesAsync();
                await hub.Clients.All.SendAsync("waCampaignProgress", new { campaignId, sent = campaign.SentCount, failed = campaign.FailedCount, total = campaign.TotalRecipients });

                // Small pacing delay between sends — avoids bursting the API.
                await Task.Delay(400);
            }

            campaign.Status = "completed";
            campaign.CompletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            await hub.Clients.All.SendAsync("waCampaignProgress", new { campaignId, sent = campaign.SentCount, failed = campaign.FailedCount, total = campaign.TotalRecipients, completed = true });
            await hub.Clients.All.SendAsync("whatsappChanged");

            logger.LogInformation("Campaign {CampaignId} completed — sent={Sent} failed={Failed} of {Total}", campaignId, campaign.SentCount, campaign.FailedCount, campaign.TotalRecipients);
        }

        [HttpGet]
        [Route("GetStatus/{id}")]
        public async Task<IActionResult> GetStatus(int id)
        {
            if (UserId == null) return Unauthorized();
            var campaign = await _db.WhatsAppCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId);
            if (campaign == null) return Json(new { success = false });

            return Json(new
            {
                success = true,
                status = campaign.Status,
                sent = campaign.SentCount,
                failed = campaign.FailedCount,
                total = campaign.TotalRecipients
            });
        }

        [HttpGet]
        [Route("GetRecipients/{id}")]
        public async Task<IActionResult> GetRecipients(int id)
        {
            if (UserId == null) return Unauthorized();
            var campaign = await _db.WhatsAppCampaigns.FirstOrDefaultAsync(c => c.Id == id && c.UserId == UserId);
            if (campaign == null) return Json(new { success = false });

            var recipients = await _db.WhatsAppCampaignRecipients
                .Where(r => r.CampaignId == id)
                .OrderBy(r => r.Id)
                .Select(r => new { r.PhoneNumber, r.Status, r.ErrorMessage, r.SentAt })
                .ToListAsync();

            return Json(new { success = true, campaignName = campaign.Name, recipients });
        }
    }

    public class CampaignListRow
    {
        public WhatsAppCampaign Campaign { get; set; } = null!;
        public int Delivered { get; set; }
        public int Undelivered { get; set; }
    }

    public class CreateCampaignRequest
    {
        public int? IntegrationId { get; set; }
        public string Name { get; set; } = "";
        public string TemplateName { get; set; } = "";
        public string LanguageCode { get; set; } = "en";
        public string? TemplateType { get; set; }
        public DateTime? ScheduledAt { get; set; }
        public List<CampaignRecipientInput> Recipients { get; set; } = new();
    }

    public class CampaignRecipientInput
    {
        public string PhoneNumber { get; set; } = "";
        public List<string>? Variables { get; set; }
    }
}
