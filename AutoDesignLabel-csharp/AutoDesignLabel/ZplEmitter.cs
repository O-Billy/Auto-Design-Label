using System.Globalization;
using System.Text;

namespace AutoDesignLabel;

/// <summary>
/// Sinh ZPL tu cung LDM. Hai che do:
///   EmitStoredFormat  - nap template mot lan vao may in (^DF), truong bien la ^FN
///   EmitPrintJob      - lenh in ngan gon (^XF + ^FN), tuong duong "fill data" cua CS6
/// </summary>
public sealed class ZplEmitter
{
    private readonly int _dpi;
    private readonly double _dpmm;

    public ZplEmitter(int dpi = 203) { _dpi = dpi; _dpmm = dpi / 25.4; }

    private int D(double mm) => (int)Math.Round(mm * _dpmm);

    /// <summary>Escape cho ^FD: chi ^ ~ \ _ va xuong dong moi nguy hiem, dung ma hex voi escape char
    /// '_' (vd '_' -> "_5F"). needsHex bao co it nhat 1 ky tu duoc escape hay khong - CHI KHI DO moi
    /// duoc phep phat ^FH truoc ^FD, vi may in/viewer chi giai ma nguoc "_XX" thanh byte khi co ^FH
    /// khai bao truoc field do; thieu ^FH, chuoi da escape se in ra nguyen van (vd "_5F" thay vi
    /// "_") - day la loi thuc te da xac nhan qua Labelary.</summary>
    private static string Fd(string s, out bool needsHex)
    {
        var sb = new StringBuilder();
        needsHex = false;
        foreach (var ch in s)
        {
            var esc = ch switch
            {
                '^' => "_5E",
                '~' => "_7E",
                '\\' => "_5C",
                '_' => "_5F",
                '\n' => "_0A",
                '\r' => "_0D",
                _ => null
            };
            if (esc is null) { sb.Append(ch); continue; }
            sb.Append(esc);
            needsHex = true;
        }
        return sb.ToString();
    }

    public string EmitStoredFormat(LabelDef label, IReadOnlyDictionary<string, string> sample)
    {
        var fieldNo = 0;
        var map = new Dictionary<string, int>();
        var sb = new StringBuilder();

        sb.AppendLine("^XA");
        sb.AppendLine($"^DFR:{label.Id.ToUpperInvariant()}.ZPL^FS");
        sb.AppendLine("^CI28");                                    // UTF-8
        sb.AppendLine($"^PW{D(label.WidthMm)}^LL{D(label.HeightMm)}^LH0,0");

        foreach (var el in Binder.Expand(label, sample))
        {
            switch (el.Type)
            {
                case "text":
                {
                    var h = D(el.Size * 25.4 / 72);
                    sb.AppendLine($"^FO{D(el.X)},{D(el.Y - el.Size * 0.72 / 2.835)}");
                    // Nhan Auto (FontFamily != null, khong theo spec typographic PMD) -> font ban dung
                    // scalable cua may in (^A0), khong phu thuoc font JioType phai nap san.
                    sb.AppendLine(el.FontFamily is not null
                        ? $"^A0N,{h},{h}"
                        : $"^A@N,{h},{h},{(el.Font == "medium" ? "E:JIOMED.TTF" : "E:JIOLGT.TTF")}");
                    sb.AppendLine(Slot(el.Text!, ref fieldNo, map) + "^FS");
                    break;
                }
                case "barcode128":
                {
                    var modules = new ZXing.OneD.Code128Writer().encode(
                        Binder.Bind(el.Data!, sample)).Length;
                    var by = Math.Max(1, (int)Math.Round(el.Width * _dpmm / modules));
                    sb.AppendLine($"^FO{D(el.X)},{D(el.Y)}^BY{by},2.0,{D(el.Height)}");
                    sb.AppendLine($"^BCN,{D(el.Height)},N,N,N");
                    sb.AppendLine(Slot(el.Data!, ref fieldNo, map) + "^FS");
                    break;
                }
                case "qr":
                {
                    var n = ZXing.QrCode.Internal.Encoder.encode(
                        Binder.Bind(el.Data!, sample),
                        ZXing.QrCode.Internal.ErrorCorrectionLevel.M).Matrix.Width;
                    var mag = Math.Max(1, (int)(el.Size * _dpmm / (n + 8)));
                    sb.AppendLine($"^FO{D(el.X)},{D(el.Y)}^BQN,2,{mag},{el.Ecc},7");
                    sb.AppendLine(Slot(el.Data!, ref fieldNo, map, qrPrefix: el.Ecc + "A,") + "^FS");
                    break;
                }
                case "image":
                    sb.AppendLine($"^FO{D(el.X)},{D(el.Y)}^XGE:{el.Placeholder}.GRF,1,1^FS");
                    break;
            }
        }

        sb.AppendLine("^XZ");
        return sb.ToString();
    }

    /// <summary>Truong tinh -> ^FD (tu them ^FH_ neu noi dung co ky tu can escape); truong co
    /// {{token}} -> ^FN de fill luc in (^FN khong doc hex nen khong can ^FH).</summary>
    private string Slot(string raw, ref int no, Dictionary<string, int> map, string qrPrefix = "")
    {
        if (!raw.Contains("{{"))
        {
            var escaped = Fd(raw, out var needsHex);
            return (needsHex ? "^FH_" : "") + $"^FD{qrPrefix}{escaped}";
        }
        if (!map.TryGetValue(raw, out var n)) { n = ++no; map[raw] = n; }
        return $"^FN{n}";
    }

    public string EmitPrintJob(LabelDef label, IReadOnlyDictionary<string, string> data, int copies = 1)
    {
        var fieldNo = 0;
        var map = new Dictionary<string, int>();
        var sb = new StringBuilder();
        sb.AppendLine("^XA");
        sb.AppendLine($"^XFR:{label.Id.ToUpperInvariant()}.ZPL");

        foreach (var el in Binder.Expand(label, data))
        {
            var raw = el.Text ?? el.Data;
            if (raw is null || !raw.Contains("{{")) continue;
            if (!map.TryGetValue(raw, out var n)) { n = ++fieldNo; map[raw] = n; }
            var prefix = el.Type == "qr" ? el.Ecc + "A," : "";
            var escaped = Fd(Binder.Bind(raw, data), out var needsHex);
            var fh = needsHex ? "^FH_" : "";
            sb.AppendLine($"^FN{n}{fh}^FD{prefix}{escaped}^FS");
        }

        sb.AppendLine($"^PQ{copies.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine("^XZ");
        return sb.ToString();
    }
}
