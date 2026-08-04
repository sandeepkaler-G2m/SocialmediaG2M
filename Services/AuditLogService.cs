namespace SocialMediaPanel.Services
{
    /// <summary>
    /// File-based audit trail — no DB table (this app has no EF migrations,
    /// see project notes). Appends one line per consequential action to
    /// App_Data/audit-log.txt. Good enough for a single-operator panel;
    /// would need a real table + pagination if this ever needs to scale to
    /// many concurrent editors.
    /// </summary>
    public class AuditLogService
    {
        private readonly string _logPath;
        private static readonly object _lock = new();

        public AuditLogService(IWebHostEnvironment env)
        {
            var dir = Path.Combine(env.ContentRootPath, "App_Data");
            Directory.CreateDirectory(dir);
            _logPath = Path.Combine(dir, "audit-log.txt");
        }

        public void Log(int? userId, string action, string details)
        {
            var line = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC | user={userId?.ToString() ?? "-"} | {action} | {details}";
            lock (_lock)
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
        }

        public List<string> GetRecent(int count = 200)
        {
            if (!File.Exists(_logPath)) return new List<string>();
            lock (_lock)
            {
                return File.ReadAllLines(_logPath).Reverse().Take(count).ToList();
            }
        }
    }
}
