using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// Standalone helper for the WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf).
    /// Finds exact text in a plain/flat PDF (no form fields needed — this is a real
    /// notice/letter with real example values already printed on it) and returns
    /// where it is — plus what it looks like there (bold vs regular, and its color),
    /// read straight off the original glyphs — so PdfTextReplaceService can white it
    /// out and draw new text that matches the surrounding document's own styling
    /// instead of a fixed guess. Coordinates are PDF-native (origin bottom-left, Y
    /// increasing up) — PdfTextReplaceService converts to PdfSharp's drawing
    /// coordinates.
    /// </summary>
    public class PdfTextLocatorService
    {
        public record TextLocation(
            int PageNumber, double X0, double Y0, double X1, double Y1,
            bool Bold, byte ColorR, byte ColorG, byte ColorB);

        /// <summary>
        /// Finds every place in the PDF where <paramref name="searchText"/> appears,
        /// in reading order (page, then top-to-bottom, then left-to-right). Matches
        /// either a single word token or a run of consecutive word tokens on the same
        /// line whose text joins (space-separated) into the search text.
        /// </summary>
        public List<TextLocation> FindAll(byte[] pdfBytes, string searchText)
        {
            var target = searchText?.Trim() ?? "";
            var results = new List<TextLocation>();
            if (target.Length == 0) return results;

            using var doc = PdfDocument.Open(new MemoryStream(pdfBytes));
            var targetWords = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            foreach (var page in doc.GetPages())
            {
                var words = page.GetWords().ToList();

                for (var i = 0; i < words.Count; i++)
                {
                    Word[]? matchedWords = null;

                    if (targetWords.Length == 1)
                    {
                        if (string.Equals(words[i].Text, target, StringComparison.Ordinal))
                            matchedWords = new[] { words[i] };
                    }
                    else if (i + targetWords.Length <= words.Count)
                    {
                        var isMatch = true;
                        for (var j = 0; j < targetWords.Length; j++)
                        {
                            if (!string.Equals(words[i + j].Text, targetWords[j], StringComparison.Ordinal))
                            {
                                isMatch = false;
                                break;
                            }
                        }
                        if (isMatch) matchedWords = words.Skip(i).Take(targetWords.Length).ToArray();
                    }

                    if (matchedWords == null) continue;

                    var first = matchedWords[0].BoundingBox;
                    var last = matchedWords[^1].BoundingBox;
                    var (bold, r, g, b) = DetectStyle(matchedWords);

                    results.Add(new TextLocation(
                        page.Number,
                        Math.Min(first.Left, last.Left),
                        Math.Min(first.Bottom, last.Bottom),
                        Math.Max(first.Right, last.Right),
                        Math.Max(first.Top, last.Top),
                        bold, r, g, b));
                }
            }

            // Reading order: page, then top-to-bottom (PDF Y increases upward, so
            // descending Y is top-to-bottom), then left-to-right.
            return results
                .OrderBy(r => r.PageNumber)
                .ThenByDescending(r => r.Y1)
                .ThenBy(r => r.X0)
                .ToList();
        }

        /// <summary>
        /// Reads bold-ness and color straight off the matched glyphs (majority vote
        /// across letters, in case of any inconsistency) instead of assuming a fixed
        /// style — so replacement text matches THIS document's own look, whatever it is.
        /// </summary>
        private static (bool Bold, byte R, byte G, byte B) DetectStyle(IEnumerable<Word> words)
        {
            var letters = words.SelectMany(w => w.Letters).ToList();
            if (letters.Count == 0) return (false, 0, 0, 0);

            var bold = letters.Count(l => l.Font?.IsBold == true) * 2 > letters.Count;

            // IColor.ToRGBValues() gives normalized 0..1 doubles, not 0..255 bytes.
            double rSum = 0.0, gSum = 0.0, bSum = 0.0;
            foreach (var l in letters)
            {
                var (r, g, b) = l.Color.ToRGBValues();
                rSum += r;
                gSum += g;
                bSum += b;
            }
            var n = letters.Count;
            return (bold,
                (byte)Math.Clamp(rSum / n * 255, 0, 255),
                (byte)Math.Clamp(gSum / n * 255, 0, 255),
                (byte)Math.Clamp(bSum / n * 255, 0, 255));
        }
    }
}
