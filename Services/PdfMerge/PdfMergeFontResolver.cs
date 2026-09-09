using PdfSharp.Fonts;

namespace SocialMediaPanel.Services.PdfMerge
{
    /// <summary>
    /// PdfSharp 6.x dropped its GDI/system-font dependency (needed for the Linux
    /// deploy server anyway), so it requires an explicit IFontResolver + font file
    /// even for a "standard" font. Sample PDFs almost always embed a generic-named
    /// subset font (this project's own test document does — PdfPig reports its
    /// family as just "CIDFont+F6", the real name isn't recoverable), so matching a
    /// specific uploaded PDF's exact typeface isn't reliably possible. Bundles
    /// Carlito instead — metric- and shape-compatible with Calibri (SIL Open Font
    /// License, see App_Data/Fonts/LICENSE_CARLITO.txt) — since Calibri is the
    /// default body font in Word/LibreOffice and so the most common typeface in
    /// business letters/notices like the ones this feature edits. Bold/regular,
    /// size, and color ARE matched per document (see PdfTextLocatorService /
    /// PdfTextReplaceService) — only the typeface itself is a fixed best-effort
    /// default.
    /// </summary>
    public class PdfMergeFontResolver : IFontResolver
    {
        public const string FamilyName = "PdfMergeSans";
        private const string RegularFace = "PdfMergeSans#regular";
        private const string BoldFace = "PdfMergeSans#bold";

        private readonly string _fontDir;

        public PdfMergeFontResolver(string fontDir)
        {
            _fontDir = fontDir;
        }

        public byte[] GetFont(string faceName)
        {
            var fileName = faceName == BoldFace ? "Carlito-Bold.ttf" : "Carlito-Regular.ttf";
            return File.ReadAllBytes(Path.Combine(_fontDir, fileName));
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
        {
            if (!string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase))
                return null;
            return new FontResolverInfo(bold ? BoldFace : RegularFace);
        }
    }
}
