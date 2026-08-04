using Microsoft.AspNetCore.Mvc;

namespace SocialMediaPanel.Controllers
{
    public class MediaLibraryItem
    {
        public string Url { get; set; } = "";
        public string FileName { get; set; } = "";
        public DateTime UploadedAt { get; set; }
        public long SizeBytes { get; set; }
    }

    /// <summary>
    /// Media Library — no DB table. wwwroot/uploads already accumulates every
    /// image PublishPost saves; this just lists that folder as a reusable
    /// gallery instead of forcing a fresh upload every time.
    /// </summary>
    public class MediaController : Controller
    {
        private readonly IWebHostEnvironment _env;

        public MediaController(IWebHostEnvironment env)
        {
            _env = env;
        }

        [HttpGet]
        public IActionResult Index()
        {
            if (HttpContext.Session.GetInt32("UserId") == null)
                return RedirectToAction("Login", "Account");

            var uploadsDir = Path.Combine(_env.WebRootPath, "uploads");
            var items = new List<MediaLibraryItem>();

            if (Directory.Exists(uploadsDir))
            {
                var validExt = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                foreach (var file in Directory.GetFiles(uploadsDir))
                {
                    var ext = Path.GetExtension(file).ToLower();
                    if (!validExt.Contains(ext)) continue;

                    var info = new FileInfo(file);
                    items.Add(new MediaLibraryItem
                    {
                        Url = "/uploads/" + info.Name,
                        FileName = info.Name,
                        UploadedAt = info.CreationTimeUtc,
                        SizeBytes = info.Length
                    });
                }
            }

            return View(items.OrderByDescending(i => i.UploadedAt).ToList());
        }
    }
}
