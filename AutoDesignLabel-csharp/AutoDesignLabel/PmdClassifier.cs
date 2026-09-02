namespace AutoDesignLabel;

/// <summary>
/// Quyet dinh mot PMD.pdf di duong nao, bang cach DUNG CHINH PmdExtractor lam oracle:
///   - Neu PmdExtractor doc duoc it nhat 1 nhan CO element (toa do vector that) -> "vector",
///     tra luon ket qua do (khong trich xuat lai lan 2).
///   - Nguoc lai (loi, khong tim thay muc nhan, hoac co nhan nhung khong nhan nao co toa do vi
///     mockup la anh raster) -> "content-only" -> ContentSpecExtractor + AutoLayoutEngine.
///
/// Reason dung de hien cho user; Web UI cho phep override thu cong.
/// </summary>
public static class PmdClassifier
{
    public sealed record Result(bool LooksVector, string Reason, LdmDocument? VectorDoc);

    public static Result Classify(byte[] pdfBytes, string sourceFileName)
    {
        LdmDocument vec;
        try
        {
            vec = new PmdExtractor().Extract(pdfBytes, sourceFileName);
        }
        catch (Exception ex)
        {
            return new Result(false,
                $"PmdExtractor loi ({ex.GetType().Name}) - xu ly nhu PMD content-only.", null);
        }

        var withElements = vec.Labels.Count(l => l.Elements.Count > 0);
        if (withElements > 0)
            return new Result(true,
                $"PmdExtractor doc duoc {withElements}/{vec.Labels.Count} nhan tu toa do vector.", vec);

        if (vec.Labels.Count == 0)
            return new Result(false,
                "PmdExtractor khong tim thay muc nhan nao (PDF khong theo dang '<n>. Ten 000.00000.000') " +
                "- xu ly nhu PMD content-only.", null);

        return new Result(false,
            $"PmdExtractor tim thay {vec.Labels.Count} nhan nhung khong nhan nao co toa do vector " +
            "(mockup co the la anh raster) - xu ly nhu PMD content-only.", null);
    }
}
