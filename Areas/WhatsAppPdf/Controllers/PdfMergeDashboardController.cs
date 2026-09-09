using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services.PdfMerge;

namespace SocialMediaPanel.Areas.WhatsAppPdf.Controllers
{
    /// <summary>
    /// User dashboard for the WhatsApp-PDF Mail-Merge feature: upload Excel + sample
    /// PDF, Generate, see this user's own batch history and per-batch generated PDFs.
    /// </summary>
    [Area("whatsapppdf")]
    [Route("whatsapppdf")]
    public class PdfMergeDashboardController : Controller
    {
        private readonly AppDbContext _db;
        private readonly PdfMergeAuthService _auth;
        private readonly PdfMergeBatchService _batchService;
        private readonly ILogger<PdfMergeDashboardController> _logger;

        public PdfMergeDashboardController(
            AppDbContext db,
            PdfMergeAuthService auth,
            PdfMergeBatchService batchService,
            ILogger<PdfMergeDashboardController> logger)
        {
            _db = db;
            _auth = auth;
            _batchService = batchService;
            _logger = logger;
        }

        private IActionResult? RequireLogin(out int userId)
        {
            var id = _auth.GetUserId(HttpContext.Session);
            if (id == null)
            {
                userId = 0;
                return RedirectToAction("Login", "PdfMergeAuth");
            }
            userId = id.Value;
            return null;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            var redirect = RequireLogin(out var userId);
            if (redirect != null) return redirect;

            var batches = await _db.PdfMergeBatches
                .Where(b => b.UserId == userId)
                .OrderByDescending(b => b.CreatedAt)
                .Take(50)
                .ToListAsync();

            ViewBag.Username = HttpContext.Session.GetString(PdfMergeAuthService.SessionUsername);
            ViewBag.IsAdmin = _auth.IsAdmin(HttpContext.Session);
            return View(batches);
        }

        [HttpPost("generate")]
        [RequestSizeLimit(104857600)]
        [RequestFormLimits(MultipartBodyLengthLimit = 104857600)]
        public async Task<IActionResult> Generate(IFormFile excelFile, IFormFile samplePdfFile)
        {
            var redirect = RequireLogin(out var userId);
            if (redirect != null) return redirect;

            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["Error"] = "Excel file chuno.";
                return RedirectToAction("Index");
            }
            if (samplePdfFile == null || samplePdfFile.Length == 0)
            {
                TempData["Error"] = "Sample PDF chuno.";
                return RedirectToAction("Index");
            }

            try
            {
                using var excelStream = excelFile.OpenReadStream();
                using var pdfMs = new MemoryStream();
                await samplePdfFile.CopyToAsync(pdfMs);

                var result = await _batchService.GenerateAsync(
                    userId, excelStream, excelFile.FileName,
                    pdfMs.ToArray(), samplePdfFile.FileName,
                    $"{Request.Scheme}://{Request.Host}",
                    HttpContext.RequestAborted);

                return RedirectToAction("Batch", new { id = result.BatchId });
            }
            catch (InvalidOperationException ex)
            {
                TempData["Error"] = ex.Message;
                return RedirectToAction("Index");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PdfMerge: generate failed for user {UserId}", userId);
                TempData["Error"] = "Generate karte waqt error aaya: " + ex.Message;
                return RedirectToAction("Index");
            }
        }

        [HttpGet("batch/{id:int}")]
        public async Task<IActionResult> Batch(int id)
        {
            var redirect = RequireLogin(out var userId);
            if (redirect != null) return redirect;

            var batch = await _db.PdfMergeBatches.FirstOrDefaultAsync(b => b.Id == id);
            if (batch == null) return NotFound();

            var isAdmin = _auth.IsAdmin(HttpContext.Session);
            // StatusCode(403), not Forbid() — Forbid() needs a configured ASP.NET Core
            // auth scheme, which this session-based app doesn't have (see WebhookController.cs).
            if (batch.UserId != userId && !isAdmin) return StatusCode(403, "Forbidden — not your batch.");

            var records = await _db.PdfMergeRecords
                .Where(r => r.BatchId == id)
                .OrderBy(r => r.RowIndex)
                .ToListAsync();

            ViewBag.Batch = batch;
            ViewBag.IsAdmin = isAdmin;
            return View(records);
        }
    }
}
