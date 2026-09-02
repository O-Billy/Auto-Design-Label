namespace AutoDesignLabel;

/// <summary>1 dong chu OCR doc duoc tu anh nhan (raster).</summary>
public sealed record OcrLine(string Text, float Confidence);

/// <summary>1 ma vach GIAI MA DUOC tu anh nhan - gia tri CHINH XAC (co checksum), khong nhieu nhu OCR.</summary>
public sealed record DecodedBarcode(string Format, string Value);

/// <summary>
/// Doc thong tin tu anh raster cua nhan trong PMD content-only (PPT -> PDF):
///   - Barcodes(): GIAI MA ma vach (Code128/UPC/EAN/QR) -> gia tri chinh xac. Day la nguon TIN CAY
///     NHAT de auto-fill gia tri mau (part number / serial / UPC deu nam trong ma vach).
///   - Ocr(): doc chu (HRI, mo ta, xuat xu). Nhieu hon, chi dung khi khong giai ma duoc ma vach
///     hoac cho cac field chi co chu.
///
/// Tach interface de phan con lai chay duoc voi NullImageReader (khong doc anh).
/// </summary>
public interface IPmdImageReader
{
    IReadOnlyList<DecodedBarcode> Barcodes(byte[] imageBytes);
    IReadOnlyList<OcrLine> Ocr(byte[] imageBytes);
}

public sealed class NullImageReader : IPmdImageReader
{
    public IReadOnlyList<DecodedBarcode> Barcodes(byte[] imageBytes) => Array.Empty<DecodedBarcode>();
    public IReadOnlyList<OcrLine> Ocr(byte[] imageBytes) => Array.Empty<OcrLine>();
}
