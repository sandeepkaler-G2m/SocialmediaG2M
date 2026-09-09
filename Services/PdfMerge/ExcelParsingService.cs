using ClosedXML.Excel;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// Standalone helper for the WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf).
    /// Reads an uploaded .xlsx into a header list + one column-&gt;value dictionary per
    /// data row. First row = headers; column names are whatever the sheet has —
    /// nothing about them is assumed here (that matching happens in PdfFormFillService).
    /// </summary>
    public class ExcelParsingService
    {
        public class ParsedExcel
        {
            public List<string> Headers { get; set; } = new();
            public List<Dictionary<string, string>> Rows { get; set; } = new();
        }

        public ParsedExcel Parse(Stream excelStream)
        {
            using var workbook = new XLWorkbook(excelStream);
            var sheet = workbook.Worksheets.First();
            var usedRange = sheet.RangeUsed();

            if (usedRange == null)
                throw new InvalidOperationException("Excel sheet khali hai.");

            var rows = usedRange.RowsUsed().ToList();
            if (rows.Count < 2)
                throw new InvalidOperationException("Excel mein header row ke alawa koi data row nahi mili.");

            var headerRow = rows[0];
            var headers = headerRow.Cells()
                .Select(c => c.GetString().Trim())
                .Where(h => !string.IsNullOrWhiteSpace(h))
                .ToList();

            if (headers.Count == 0)
                throw new InvalidOperationException("Excel ki pehli row mein column headers nahi mile.");

            var result = new ParsedExcel { Headers = headers };

            for (int r = 1; r < rows.Count; r++)
            {
                var row = rows[r];
                var record = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int c = 0; c < headers.Count; c++)
                {
                    var cell = row.Cell(c + 1);
                    record[headers[c]] = cell.GetString().Trim();
                }

                // Skip fully-empty rows (trailing blank rows Excel sometimes includes in the used range)
                if (record.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
                    result.Rows.Add(record);
            }

            if (result.Rows.Count == 0)
                throw new InvalidOperationException("Excel mein header ke baad koi non-empty data row nahi mili.");

            return result;
        }
    }
}
