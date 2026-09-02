namespace AutoDesignLabel;

/// <summary>
/// "Content spec" - model trung gian giua PMD dang content-only (PPT -> PDF, khong co toa do
/// vector) va AutoLayoutEngine. KHONG phai LDM: chi la danh sach field + gia tri mau + kich thuoc
/// nhan da trich duoc; AutoLayoutEngine moi la buoc bien no thanh LabelDef co toa do.
///
/// Duong nay chay khi PmdClassifier ket luan file KHONG phai vector (xem PmdClassifier). PMD dang
/// nay chi rang buoc kich thuoc nhan + danh sach field bat buoc - khong quy dinh font, khong quy
/// dinh loai barcode - nen moi thu con lai la MAC DINH, cho user review/doi sau.
/// </summary>
public enum ContentRenderKind { Text, Barcode, Table }

public sealed class ContentField
{
    /// <summary>Ten token chuan hoa, vd "PART_NUMBER" - dung lam {{TOKEN}} trong element.</summary>
    public string Token { get; set; } = "";

    /// <summary>Nhan chu doc duoc tu callout PMD, vd "Part No." - dung lam prefix text tren nhan.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Gia tri mau (KHONG kem tien to data identifier). Null cho den khi doc anh / user dien.</summary>
    public string? SampleValue { get; set; }

    /// <summary>Tien to ANSI MH10 data identifier ma vach that dung (vd "1P" = supplier part,
    /// "S" = serial, "Q" = quantity) - doc duoc khi giai ma ma vach trong anh. Ma vach sinh ra se
    /// ma hoa "DI + gia tri" cho khop artwork da duyet, con HRI/caption chi hien gia tri.</summary>
    public string? DataIdentifier { get; set; }

    public ContentRenderKind Kind { get; set; } = ContentRenderKind.Text;

    /// <summary>"pdfText" | "ocr" | "user" - nguon cua Caption/SampleValue.</summary>
    public string Source { get; set; } = "pdfText";

    public double Confidence { get; set; } = 0.5;
}

public sealed class ContentLabel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Product { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }

    /// <summary>"simple-id" | "packing" | "lit-kit" | "unknown" - quyet dinh AutoLayoutEngine
    /// dung ham layout nao.</summary>
    public string Archetype { get; set; } = "unknown";

    public List<ContentField> Fields { get; set; } = new();

    /// <summary>Cac dong bang kit (Part Number, Serial Number) da resolve duoc (chi nhan lit-kit).
    /// AutoLayoutEngine dung truc tiep neu co, thay vi doi data KIT_PN{i}/KIT_SN{i}.</summary>
    public List<KitRow> KitRows { get; set; } = new();

    /// <summary>Toan bo dong chu/ma vach doc duoc tu anh nhan - de buoc review hien cho user doi
    /// chieu / sua.</summary>
    public List<string> OcrRawLines { get; set; } = new();

    /// <summary>Cac diem can nguoi dung xem lai -> se thanh OpenIssue trong LDM.</summary>
    public List<string> Notes { get; set; } = new();
}

public sealed record KitRow(string PartNumber, string Serial);

public sealed class ContentSpec
{
    public string DocumentId { get; set; } = "UNKNOWN";
    public string Revision { get; set; } = "00";
    public string SourcePdf { get; set; } = "";
    public List<ContentLabel> Labels { get; set; } = new();
    public List<string> Notes { get; set; } = new();
}
