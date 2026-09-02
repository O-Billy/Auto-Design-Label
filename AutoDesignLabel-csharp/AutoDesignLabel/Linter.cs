namespace AutoDesignLabel;

public enum LintStatus { Ok, Warning, Error }

public sealed record LintRow(string Label, string Kind, string Detail, LintStatus Status);

/// <summary>
/// Printability linter - bat cac loi chi lo ra khi da in hang loat:
/// X-dimension qua nho, QR khong du cho, chu tran mep, phan tu chong nhau.
/// </summary>
public sealed class Linter
{
    public const double MinXDimMm = 0.19;      // Code128, may quet cam tay pho thong
    public const double MinQrModuleMm = 0.25;
    public static readonly int[] DpiTargets = { 203, 300 };

    private readonly List<LintRow> _rows = new();
    public IReadOnlyList<LintRow> Rows => _rows;
    public bool HasError => _rows.Any(r => r.Status == LintStatus.Error);

    public void Code128(string label, string value, int modules, double targetWidthMm)
    {
        var xDim = targetWidthMm / modules;
        var fits = string.Join(", ", DpiTargets.Select(dpi =>
        {
            var dpmm = dpi / 25.4;
            var n = Math.Max(1, (int)Math.Round(xDim * dpmm));
            return $"{dpi}dpi: ^BY{n} -> {modules * n / dpmm:F1}mm (X={n / dpmm:F3}mm)";
        }));

        _rows.Add(new LintRow(label, "code128",
            $"{value} | {modules} module | dich {targetWidthMm}mm | X={xDim:F4}mm | {fits}",
            xDim >= MinXDimMm ? LintStatus.Ok : LintStatus.Warning));
    }

    public void Qr(string label, string value, int modules, double targetSizeMm)
    {
        var module = targetSizeMm / (modules + 8);
        var fits = string.Join(", ", DpiTargets.Select(dpi =>
        {
            var dpmm = dpi / 25.4;
            var mag = Math.Max(1, (int)(targetSizeMm * dpmm / (modules + 8)));
            return $"{dpi}dpi: mag {mag} -> {(modules + 8) * mag / dpmm:F1}mm";
        }));

        _rows.Add(new LintRow(label, "qr",
            $"{value.Length} byte | {modules}x{modules} o | dich {targetSizeMm}mm | " +
            $"o={module:F4}mm | {fits}",
            module >= MinQrModuleMm ? LintStatus.Ok : LintStatus.Warning));
    }

    public void TextFit(string label, string text, double x0, double widthMm, double mediaWidthMm)
    {
        if (x0 >= -0.01 && x0 + widthMm <= mediaWidthMm + 0.01) return;
        _rows.Add(new LintRow(label, "text-overflow",
            $"\"{Trunc(text)}\" chiem {x0:F1}..{x0 + widthMm:F1}mm / kho {mediaWidthMm}mm",
            LintStatus.Error));
    }

    public void Collide(string label,
        List<(string Kind, string Name, double X1, double Y1, double X2, double Y2)> boxes)
    {
        for (var i = 0; i < boxes.Count; i++)
        for (var j = i + 1; j < boxes.Count; j++)
        {
            var (k1, n1, ax1, ay1, ax2, ay2) = boxes[i];
            var (k2, n2, bx1, by1, bx2, by2) = boxes[j];
            if (k1 == "text" && k2 == "text") continue;   // chu ke chu la binh thuong

            var ox = Math.Min(ax2, bx2) - Math.Max(ax1, bx1);
            var oy = Math.Min(ay2, by2) - Math.Max(ay1, by1);
            if (ox <= 0.3 || oy <= 0.3) continue;

            _rows.Add(new LintRow(label, "collision",
                $"{k1}:{Trunc(n1)} <-> {k2}:{Trunc(n2)} chong {ox:F1}x{oy:F1}mm",
                LintStatus.Error));
        }
    }

    private static string Trunc(string s) => s.Length <= 28 ? s : s[..28] + "...";

    public void PrintReport()
    {
        var bad = _rows.Where(r => r.Status != LintStatus.Ok).ToList();
        Console.WriteLine($"Kiem tra: {_rows.Count} muc, canh bao/loi: {bad.Count}");
        foreach (var r in bad)
            Console.WriteLine($"  [{r.Status}] {r.Label} / {r.Kind}: {r.Detail}");
    }
}
