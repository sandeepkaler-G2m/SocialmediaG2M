using System.Text.Json;
using SocialMediaPanel.Data;
using SocialMediaPanel.Models;
using SocialMediaPanel.Services;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// Standalone orchestrator for the WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf).
    ///
    /// Works on a plain/flat sample PDF — a real notice with real example values
    /// already printed on it, NOT a fillable form. The Excel's FIRST data row is the
    /// "anchor" row: it must contain the values exactly as they currently appear in
    /// the sample PDF, so each column's on-page location can be found once (via
    /// PdfTextLocatorService). Every row AFTER that is an actual record — for each
    /// one, PdfTextReplaceService paints over those same locations with that row's
    /// values, producing one real PDF per row. WhatsApp sending is NOT done here (not
    /// built yet) — phone_number is only captured for the send phase later.
    /// </summary>
    public class PdfMergeBatchService
    {
        private const string StorageFolder = "pdfmerge-generated";

        // Common header spellings for the phone-number column, compared after
        // lower-casing and stripping spaces/underscores/hyphens, e.g. "Phone Number",
        // "phone_number" and "PhoneNumber" all normalize to "phonenumber".
        private static readonly HashSet<string> PhoneColumnAliases = new()
        {
            "phone", "mobile", "whatsapp", "contact",
            "phonenumber", "mobilenumber", "whatsappnumber", "contactnumber",
            "mobileno", "phoneno", "whatsappno"
        };

        private readonly ExcelParsingService _excel;
        private readonly PdfTextLocatorService _locator;
        private readonly PdfTextReplaceService _replacer;
        private readonly AppDbContext _db;
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _config;
        private readonly ILogger<PdfMergeBatchService> _logger;

        public PdfMergeBatchService(
            ExcelParsingService excel,
            PdfTextLocatorService locator,
            PdfTextReplaceService replacer,
            AppDbContext db,
            IWebHostEnvironment env,
            IConfiguration config,
            ILogger<PdfMergeBatchService> logger)
        {
            _excel = excel;
            _locator = locator;
            _replacer = replacer;
            _db = db;
            _env = env;
            _config = config;
            _logger = logger;
        }

        public class RecordSummary
        {
            public int RowIndex { get; set; }
            public string FileName { get; set; } = "";
            public string Url { get; set; } = "";
            public string? Phone { get; set; }
        }

        public class GenerateResult
        {
            public int BatchId { get; set; }
            public int TotalRecords { get; set; }
            public List<RecordSummary> Records { get; set; } = new();
        }

        public async Task<GenerateResult> GenerateAsync(
            int userId, Stream excelStream, string excelFileName,
            byte[] samplePdfBytes, string samplePdfFileName,
            string fallbackBaseUrl, CancellationToken ct = default)
        {
            var parsed = _excel.Parse(excelStream);

            if (parsed.Rows.Count < 2)
                throw new InvalidOperationException(
                    "Excel mein kam se kam 2 rows honi chahiye: pehli row 'anchor' hai (jo values abhi PDF mein hain, unhi ka pehla row), " +
                    "uske baad ki rows asli records hain. Sirf 1 row mili.");

            var phoneColumn = DetectPhoneColumn(parsed.Headers);
            if (phoneColumn == null)
            {
                throw new InvalidOperationException(
                    "Excel mein phone number wala column nahi mila. Headers mile: "
                    + string.Join(", ", parsed.Headers)
                    + ". Column ka naam kuch aisa hona chahiye: phone, mobile, whatsapp, contact, phone_number, mobile_number.");
            }

            var anchorRow = parsed.Rows[0];
            var dataRows = parsed.Rows.Skip(1).ToList();

            var columnLocations = ResolveColumnLocations(samplePdfBytes, parsed.Headers, anchorRow);

            var batch = new PdfMergeBatch
            {
                UserId = userId,
                ExcelFileName = excelFileName,
                SamplePdfFileName = samplePdfFileName,
                PhoneColumn = phoneColumn,
                TotalRecords = dataRows.Count,
                Status = "processing",
                CreatedAt = DateTime.UtcNow
            };
            _db.PdfMergeBatches.Add(batch);
            await _db.SaveChangesAsync(ct);

            var dir = Path.Combine(_env.WebRootPath, StorageFolder, batch.Id.ToString());
            Directory.CreateDirectory(dir);

            var baseUrl = _config["AppBaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = fallbackBaseUrl.TrimEnd('/');

            var summaries = new List<RecordSummary>();
            var recordEntities = new List<PdfMergeRecord>();

            for (var i = 0; i < dataRows.Count; i++)
            {
                var row = dataRows[i];

                var replacements = new List<PdfTextReplaceService.Replacement>();
                foreach (var column in parsed.Headers)
                {
                    if (!row.TryGetValue(column, out var newValue)) continue;
                    if (!columnLocations.TryGetValue(column, out var locations)) continue;

                    foreach (var loc in locations)
                    {
                        replacements.Add(new PdfTextReplaceService.Replacement
                        {
                            PageNumber = loc.PageNumber,
                            X0 = loc.X0,
                            Y0 = loc.Y0,
                            X1 = loc.X1,
                            Y1 = loc.Y1,
                            NewText = newValue,
                            Bold = loc.Bold,
                            ColorR = loc.ColorR,
                            ColorG = loc.ColorG,
                            ColorB = loc.ColorB
                        });
                    }
                }

                byte[] pdfBytes;
                try
                {
                    pdfBytes = _replacer.Apply(samplePdfBytes, replacements);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "PdfMerge: row {Row} replace failed for batch {BatchId}", i, batch.Id);
                    throw new InvalidOperationException($"Row {i + 1} ka PDF banane mein error: {ex.Message}");
                }

                row.TryGetValue(phoneColumn, out var phone);

                var fileNameBase = !string.IsNullOrWhiteSpace(phone) ? phone
                    : row.Where(kv => !string.Equals(kv.Key, phoneColumn, StringComparison.OrdinalIgnoreCase))
                         .Select(kv => kv.Value)
                         .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
                      ?? $"record-{i + 1}";

                var fileName = GoogleDrivePdfService.SanitizeFileName(fileNameBase);
                var savePath = Path.Combine(dir, fileName);
                if (File.Exists(savePath))
                {
                    var stem = Path.GetFileNameWithoutExtension(fileName);
                    fileName = $"{stem}_{i + 1}.pdf";
                    savePath = Path.Combine(dir, fileName);
                }

                await File.WriteAllBytesAsync(savePath, pdfBytes, ct);

                var url = $"{baseUrl}/{StorageFolder}/{batch.Id}/{fileName}";

                recordEntities.Add(new PdfMergeRecord
                {
                    BatchId = batch.Id,
                    RowIndex = i,
                    DataJson = JsonSerializer.Serialize(row),
                    PhoneNumber = phone,
                    GeneratedFileName = fileName,
                    GeneratedUrl = url,
                    CreatedAt = DateTime.UtcNow
                });

                summaries.Add(new RecordSummary { RowIndex = i, FileName = fileName, Url = url, Phone = phone });
            }

            _db.PdfMergeRecords.AddRange(recordEntities);
            batch.Status = "completed";
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation(
                "PdfMerge: batch {BatchId} completed — {Count} PDFs generated for user {UserId}",
                batch.Id, recordEntities.Count, userId);

            return new GenerateResult
            {
                BatchId = batch.Id,
                TotalRecords = recordEntities.Count,
                Records = summaries
            };
        }

        /// <summary>
        /// For each Excel column, finds where its anchor-row value currently sits in
        /// the sample PDF. Two different columns sometimes share the exact same
        /// anchor value (e.g. two amount columns that both happen to read "3832" in
        /// the sample) — those are disambiguated by pairing Excel column order with
        /// PDF reading order. A column whose value can't be found (or can't be
        /// disambiguated) fails the whole batch with a clear message, since silently
        /// guessing would risk writing a value into the wrong spot on every record.
        /// </summary>
        private Dictionary<string, List<PdfTextLocatorService.TextLocation>> ResolveColumnLocations(
            byte[] samplePdfBytes, List<string> headers, Dictionary<string, string> anchorRow)
        {
            var result = new Dictionary<string, List<PdfTextLocatorService.TextLocation>>(StringComparer.OrdinalIgnoreCase);
            var unresolved = new List<string>();

            var columnsByAnchorValue = headers
                .GroupBy(h => anchorRow.TryGetValue(h, out var v) ? v : "")
                .ToList();

            foreach (var group in columnsByAnchorValue)
            {
                var anchorValue = group.Key;
                var columns = group.ToList();

                if (string.IsNullOrWhiteSpace(anchorValue))
                {
                    unresolved.AddRange(columns);
                    continue;
                }

                var locations = _locator.FindAll(samplePdfBytes, anchorValue);

                if (locations.Count == 0)
                {
                    unresolved.AddRange(columns);
                    continue;
                }

                if (columns.Count == 1)
                {
                    // Every place this value appears belongs to this one column —
                    // covers legitimate repeats (e.g. an account number shown both in
                    // the letter body and again in an annexure table).
                    result[columns[0]] = locations;
                }
                else if (locations.Count >= columns.Count)
                {
                    // Ambiguous: N different columns share one anchor value. Pair
                    // them up by order — Excel header order against PDF reading order.
                    for (var k = 0; k < columns.Count; k++)
                        result[columns[k]] = new List<PdfTextLocatorService.TextLocation> { locations[k] };
                }
                else
                {
                    unresolved.AddRange(columns);
                }
            }

            if (unresolved.Count > 0)
            {
                throw new InvalidOperationException(
                    "Ye columns sample PDF mein nahi mile (Excel ki pehli/anchor row ki value dhoondte hue): "
                    + string.Join(", ", unresolved)
                    + ". Check karo ki Excel ki pehli row mein wahi exact text hai jo abhi PDF mein likha hai.");
            }

            return result;
        }

        private static string? DetectPhoneColumn(List<string> headers)
        {
            foreach (var h in headers)
            {
                var normalized = new string(h.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (PhoneColumnAliases.Contains(normalized))
                    return h;
            }
            return null;
        }
    }
}
