using PdfSharp.Drawing;
using PdfSharp.Pdf.IO;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// Standalone helper for the WhatsApp-PDF Mail-Merge feature (Areas/WhatsAppPdf).
    /// Takes locations found by PdfTextLocatorService and, for each, paints a white
    /// box over the original text and draws the new value in its place — no form
    /// fields involved, this edits a plain/flat PDF directly. Bold/regular and color
    /// come from the original document (whatever it is) via each Replacement; the
    /// typeface itself is fixed (PdfMergeFontResolver) since a PDF's real embedded
    /// font name usually isn't recoverable.
    /// </summary>
    public class PdfTextReplaceService
    {
        public class Replacement
        {
            public int PageNumber { get; set; } // 1-based, matches PdfTextLocatorService
            public double X0 { get; set; }
            public double Y0 { get; set; }
            public double X1 { get; set; }
            public double Y1 { get; set; }
            public string NewText { get; set; } = "";
            public bool Bold { get; set; }
            public byte ColorR { get; set; }
            public byte ColorG { get; set; }
            public byte ColorB { get; set; }
        }

        public byte[] Apply(byte[] pdfBytes, IEnumerable<Replacement> replacements)
        {
            using var input = new MemoryStream(pdfBytes);
            using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

            foreach (var group in replacements.GroupBy(r => r.PageNumber))
            {
                var pageIndex = group.Key - 1;
                if (pageIndex < 0 || pageIndex >= doc.Pages.Count) continue;

                var page = doc.Pages[pageIndex];
                var pageHeight = page.Height.Point;

                using var gfx = XGraphics.FromPdfPage(page);
                foreach (var r in group)
                {
                    // PdfTextLocatorService gives PDF-native coords (origin bottom-left,
                    // Y increasing up); PdfSharp's XGraphics on an existing page is
                    // top-left origin, Y increasing down (verified empirically) — flip.
                    var boxHeight = r.Y1 - r.Y0;
                    var topDownTop = pageHeight - r.Y1;

                    const double padX = 1.5;
                    const double padY = 1.0;
                    gfx.DrawRectangle(
                        XBrushes.White,
                        r.X0 - padX, topDownTop - padY,
                        (r.X1 - r.X0) + 2 * padX, boxHeight + 2 * padY);

                    // Font size close to the box height so replacement text reads at
                    // roughly the same size as what it's replacing, capped so it
                    // never looks oversized if the original text was unusually short.
                    var fontSize = Math.Clamp(boxHeight * 0.82, 7.5, 12.5);
                    var style = r.Bold ? XFontStyleEx.Bold : XFontStyleEx.Regular;
                    var font = new XFont(PdfMergeFontResolver.FamilyName, fontSize, style);
                    var brush = new XSolidBrush(XColor.FromArgb(r.ColorR, r.ColorG, r.ColorB));

                    var rect = new XRect(r.X0, topDownTop, Math.Max(r.X1 - r.X0, 400), boxHeight);
                    gfx.DrawString(r.NewText, font, brush, rect, XStringFormats.CenterLeft);
                }
            }

            using var output = new MemoryStream();
            doc.Save(output);
            return output.ToArray();
        }
    }
}
