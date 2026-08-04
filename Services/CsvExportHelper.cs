using System.Text;

namespace SocialMediaPanel.Services
{
    /// <summary>
    /// Minimal CSV writer — the data exported by this app (leads, posts,
    /// insight rows) is simple enough that a small manual escaper covers it
    /// without pulling in a CSV library dependency.
    /// </summary>
    public static class CsvExportHelper
    {
        public static string BuildCsv(IEnumerable<string> headers, IEnumerable<IEnumerable<object?>> rows)
        {
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", headers.Select(Escape)));
            foreach (var row in rows)
                sb.AppendLine(string.Join(",", row.Select(v => Escape(v?.ToString() ?? ""))));
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
