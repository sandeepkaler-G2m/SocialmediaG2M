using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SocialMediaPanel.Data;
using SocialMediaPanel.ViewModels;

namespace SocialMediaPanel.Controllers
{
    public class DashboardController : Controller
    {
        private readonly AppDbContext _context;

        public DashboardController(AppDbContext context)
        {
            _context = context;
        }

        // ==================== DASHBOARD INDEX ====================
        public async Task<IActionResult> Index()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";

            return View();
        }

        // ==================== COMPOSE POST ====================
        // Dashboard "Compose Post" button → Dashboard/Compose view
        public async Task<IActionResult> Compose()
        {
            var userId = HttpContext.Session.GetInt32("UserId");
            if (userId == null)
                return RedirectToAction("Login", "Account");

            var user = await _context.Users
                .Where(u => u.Id == userId)
                .FirstOrDefaultAsync();

            if (user == null)
            {
                HttpContext.Session.Clear();
                return RedirectToAction("Login", "Account");
            }

            ViewBag.UserName = user.Name;
            ViewBag.UserEmail = user.Email;
            ViewBag.CompanyName = user.CompanyName ?? "Your Workspace";

            return View(); // Views/Dashboard/Compose.cshtml load hoga
        }



        [HttpGet]
        public async Task<IActionResult> Leads()
        {
            var leads = await _context.Leads
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new LeadViewModel
                {
                    Id = l.Id,
                    LeadId = l.LeadId,
                    PageId = l.PageId,
                    FormId = l.FormId,
                    FullName = l.FullName,
                    Email = l.Email,
                    Phone = l.Phone,
                    Platform = l.Platform,
                    RawData = l.RawData,
                    Status = l.Status ?? "open",
                    CreatedAt = l.CreatedAt
                })
                .ToListAsync();

            return View(leads);
        }

        [HttpGet]
        [Route("Leads/GetAll")]
        public async Task<IActionResult> GetAll(string? status)
        {
            var query = _context.Leads.AsQueryable();

            if (!string.IsNullOrEmpty(status) && status != "all")
               query = query.Where(l => l.Status == status);

            var leads = await query
                .OrderByDescending(l => l.CreatedAt)
                .Select(l => new
                {
                    id = l.Id,
                    leadId = l.LeadId,
                    name = l.FullName ?? "(No name)",
                    email = l.Email ?? "",
                    phone = l.Phone ?? "",
                    platform = l.Platform ?? "",
                    formId = l.FormId ?? "",
                    pageId = l.PageId ?? "",
                    rawData = l.RawData ?? "",
                    status = (l.Status ?? "open").ToLower(),
                    createdAt = l.CreatedAt.ToString("MMM dd, yyyy h:mm tt")
                })
                .ToListAsync();

            return Json(leads);
        }

        // ── UpdateStatus — AJAX status change ───────────────────────
        [HttpPost]
        [Route("Leads/UpdateStatus")]
        public async Task<IActionResult> UpdateStatus([FromBody] UpdateStatusRequest request)
        {
            var lead = await _context.Leads.FindAsync(request.Id);
            if (lead == null)
                return Json(new { success = false, message = "Lead not found." });

            lead.Status = request.Status;
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // ── Delete — AJAX delete ─────────────────────────────────────
        [HttpPost]
        [Route("Leads/Delete/{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null)
                return Json(new { success = false, message = "Lead not found." });

            _context.Leads.Remove(lead);
            await _context.SaveChangesAsync();

            return Json(new { success = true });
        }

        // ── Details — optional full detail page ─────────────────────
        [HttpGet]
        public async Task<IActionResult> Details(int id)
        {
            var lead = await _context.Leads.FindAsync(id);
            if (lead == null) return NotFound();
            return Json(lead);
        }
    }
}