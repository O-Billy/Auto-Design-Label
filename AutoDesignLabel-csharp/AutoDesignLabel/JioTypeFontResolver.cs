using PdfSharpCore.Fonts;

namespace AutoDesignLabel;

/// <summary>
/// Font resolver cho PdfSharpCore. Ho tro 2 ho font:
///
///   "JioType"  - font that theo PMD co spec typographic (vi du B01O023). Nhung san trong assembly
///                tu thu muc Font/ (xem AutoDesignLabel.csproj). Dung cho nhan LayoutSource=Exact.
///
///   "Helvetica"/"Arial" - font TRUNG TINH cho nhan LayoutSource=Auto: PMD dang content-only
///                (PPT -> PDF) KHONG quy dinh font, nen AutoLayoutEngine chi chon mot mac dinh de
///                nguoi dung review/doi sau. Lay Arial that tu thu muc Fonts cua Windows (may trien
///                khai - server Web / may CS6 - luon co Arial); neu khong tim thay (khong phai
///                Windows) thi fallback ve JioType de pipeline van chay.
///
/// PdfSharpCore khong tu tim font he thong khi da cai IFontResolver tuy chinh, nen phai tu cung cap
/// byte cho moi face qua GetFont.
/// </summary>
public sealed class JioTypeFontResolver : IFontResolver
{
    public const string FamilyName = "JioType";
    public const string NeutralFamilyName = "Helvetica";

    private const string JioLight = "JioType#Light";
    private const string JioMedium = "JioType#Medium";
    private const string NeutralRegular = "Neutral#Regular";
    private const string NeutralBold = "Neutral#Bold";

    public string DefaultFontName => JioLight;

    public byte[] GetFont(string faceName) => faceName switch
    {
        JioMedium => Embedded("JioType-Medium.ttf"),
        NeutralRegular => SystemArial("arial.ttf") ?? Embedded("JioType-Light.ttf"),
        NeutralBold => SystemArial("arialbd.ttf") ?? Embedded("JioType-Medium.ttf"),
        _ => Embedded("JioType-Light.ttf"),
    };

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
    {
        var neutral = familyName.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase)
                      || familyName.StartsWith("Arial", StringComparison.OrdinalIgnoreCase);
        if (neutral)
            return new FontResolverInfo(isBold ? NeutralBold : NeutralRegular);

        // Nhan medium = dam theo quy uoc da dung trong PdfLabelRenderer (XFontStyle.Bold).
        return new FontResolverInfo(isBold ? JioMedium : JioLight);
    }

    private static byte[] Embedded(string resourceName)
    {
        using var stream = typeof(JioTypeFontResolver).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Khong tim thay font nhung '{resourceName}' trong assembly.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[]? SystemArial(string fileName)
    {
        try
        {
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            if (string.IsNullOrEmpty(dir)) return null;
            var path = Path.Combine(dir, fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }
}
