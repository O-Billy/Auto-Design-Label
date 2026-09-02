using PdfSharpCore.Drawing;
using PdfSharpCore.Fonts;
using PdfSharpCore.Pdf;
using ZXing.OneD;
using ZXing.QrCode.Internal;

namespace AutoDesignLabel;

/// <summary>
/// Render LDM ra PDF 1:1. Barcode/QR ve bang hinh chu nhat vector nen sac net
/// o moi do phan giai - dung de duyet artwork va do kich thuoc that.
/// </summary>
public sealed class PdfLabelRenderer
{
    // Chay 1 lan duy nhat truoc khi bat ky XFont nao duoc tao - dam bao dung font that JioType
    // thay vi font he thong mac dinh, bat ke goi tu CLI hay tu Web app.
    static PdfLabelRenderer() => GlobalFontSettings.FontResolver = new JioTypeFontResolver();

    private readonly Linter _lint;
    public PdfLabelRenderer(Linter lint) => _lint = lint;

    private static XUnit Mm(double v) => XUnit.FromMillimeter(v);

    private static XFont Font(Element el) => new(
        el.FontFamily ?? JioTypeFontResolver.FamilyName,
        el.Size,
        el.Font == "medium" ? XFontStyle.Bold : XFontStyle.Regular);

    public void Render(LdmDocument doc, IReadOnlyDictionary<string, string> data, string outPath)
    {
        using var pdf = new PdfDocument();
        foreach (var label in doc.Labels)
            AddLabelPage(pdf, label, data);
        pdf.Save(outPath);
    }

    /// <summary>Render 1 nhan ra Stream trong bo nho - dung cho web UI (xem truoc trong iframe,
    /// khong can ghi file tam ra dia).</summary>
    public void RenderLabel(LabelDef label, IReadOnlyDictionary<string, string> data, Stream output)
    {
        using var pdf = new PdfDocument();
        AddLabelPage(pdf, label, data);
        pdf.Save(output);
    }

    private void AddLabelPage(PdfDocument pdf, LabelDef label, IReadOnlyDictionary<string, string> data)
    {
        var page = pdf.AddPage();
        page.Width = Mm(label.WidthMm);
        page.Height = Mm(label.HeightMm);
        using var gfx = XGraphics.FromPdfPage(page);
        DrawLabel(gfx, label, data);
    }

    // Khoang trong toi thieu (mm) giua 2 doan text lien ke CUNG mot dong (cung el.Y chinh xac -
    // dau hieu chung la 1 dong bi PmdExtractor tach thanh nhieu phan tu do doi co chu/do dam giua
    // chung, vd "1N" 12pt roi "Device +" 6pt). Toa do X trich xuat tu PDF goc phan anh do rong ky
    // tu cua FONT GOC trong PMD, nhung luc ve lai ta dung font thay the (JioType) co the co do rong
    // khac - neu khoang cach X goc qua nho, doan sau se bi de/dinh vao doan truoc. Ep buoc khoang
    // trong toi thieu nay dam bao khong bao gio dinh chu, bat ke PMD/font nao.
    private const double MinTextGapMm = 0.3;

    private void DrawLabel(XGraphics gfx, LabelDef label, IReadOnlyDictionary<string, string> data)
    {
        var boxes = new List<(string Kind, string Name, double X1, double Y1, double X2, double Y2)>();
        var elements = Binder.Expand(label, data).ToList();

        // Vien nhan (die-cut). Trang PDF = kich thuoc nhan that nen mac dinh khong ve vien; chi ve khi
        // PMD co khung bo goc (CornerRadiusMm > 0) de proof phan anh dung hinh dang die-cut. Ve luon
        // net vao ~0.15mm tranh bi xen o mep trang.
        if (label.CornerRadiusMm > 0.05)
        {
            const double insetMm = 0.15;
            gfx.DrawRoundedRectangle(new XPen(XColor.FromArgb(150, 150, 150), 0.25),
                Mm(insetMm), Mm(insetMm),
                Mm(label.WidthMm - 2 * insetMm), Mm(label.HeightMm - 2 * insetMm),
                Mm(label.CornerRadiusMm * 2), Mm(label.CornerRadiusMm * 2));
        }

        // Buoc 1 rieng cho text: do be rong thuc te (bang chinh font se dung de ve) va tinh x0 ban
        // dau cho tung phan tu, RIENG THEO TUNG DONG (cung el.Y). Elements.Expand() khong dam bao
        // thu tu trai->phai trong 1 dong (chi la thu tu trich xuat trong PmdExtractor), nen phai tu
        // sap xep lai theo x0 truoc khi ep khoang trong toi thieu - neu ep theo thu tu goc se co the
        // day nham phan tu dung dau dong ra ngoai kho nhan (xem MinTextGapMm o tren).
        var textLayout = new Dictionary<Element, (string Text, XFont Font, double W, double X0)>();
        foreach (var el in elements)
        {
            if (el.Type != "text") continue;
            var s = Binder.Bind(el.Text!, data);
            var font = Font(el);
            var w = gfx.MeasureString(s, font).Width / XUnit.FromMillimeter(1).Point;
            var x0 = el.Align switch
            {
                "right" => el.X - w,
                "center" => el.X - w / 2,
                _ => el.X
            };
            textLayout[el] = (s, font, w, x0);
        }
        foreach (var row in textLayout.ToList().GroupBy(kv => kv.Key.Y))
        {
            double? nextX = null;
            foreach (var kv in row.OrderBy(kv => kv.Value.X0))
            {
                var (s, font, w, x0) = kv.Value;
                if (kv.Key.Align != "right" && kv.Key.Align != "center" && nextX is double min && x0 < min)
                    x0 = min;
                nextX = x0 + w + MinTextGapMm;
                textLayout[kv.Key] = (s, font, w, x0);
            }
        }

        foreach (var el in elements)
        {
            switch (el.Type)
            {
                case "text":
                {
                    var (s, font, w, x0) = textLayout[el];
                    gfx.DrawString(s, font, XBrushes.Black, Mm(x0), Mm(el.Y),
                                   XStringFormats.BaseLineLeft);

                    _lint.TextFit(label.Id, s, x0, w, label.WidthMm);
                    var asc = el.Size * 0.72 / 2.835;
                    var dsc = el.Size * 0.20 / 2.835;
                    boxes.Add(("text", s, x0, el.Y - asc, x0 + w, el.Y + dsc));
                    break;
                }

                case "line":
                    gfx.DrawLine(new XPen(XColors.Black, 0.3) { DashStyle = el.Dash is null
                        ? XDashStyle.Solid : XDashStyle.Dash },
                        Mm(el.X1), Mm(el.Y1), Mm(el.X2), Mm(el.Y2));
                    break;

                case "barcode128":
                {
                    var val = Binder.Bind(el.Data!, data);
                    DrawCode128(gfx, val, el, label);
                    boxes.Add(("barcode", val, el.X, el.Y, el.X + el.Width, el.Y + el.Height));
                    break;
                }

                case "qr":
                {
                    var val = Binder.Bind(el.Data!, data);
                    DrawQr(gfx, val, el, label);
                    boxes.Add(("qr", "QR", el.X, el.Y, el.X + el.Size, el.Y + el.Size));
                    break;
                }

                case "image":
                    gfx.DrawRectangle(new XPen(XColors.Gray, 0.25), Mm(el.X), Mm(el.Y),
                                      Mm(el.Width), Mm(el.Height));
                    boxes.Add(("image", el.Placeholder ?? "IMG",
                               el.X, el.Y, el.X + el.Width, el.Y + el.Height));
                    break;
            }
        }
        _lint.Collide(label.Id, boxes);
    }

    private void DrawCode128(XGraphics gfx, string value, Element el, LabelDef label)
    {
        bool[] modules = new Code128Writer().encode(value);
        var moduleWidthMm = el.Width / modules.Length;
        _lint.Code128(label.Id, value, modules.Length, el.Width);

        var run = 0;
        for (var i = 0; i <= modules.Length; i++)
        {
            var on = i < modules.Length && modules[i];
            if (on) { run++; continue; }
            if (run > 0)
            {
                gfx.DrawRectangle(XBrushes.Black,
                    Mm(el.X + (i - run) * moduleWidthMm), Mm(el.Y),
                    Mm(run * moduleWidthMm), Mm(el.Height));
                run = 0;
            }
        }
    }

    private void DrawQr(XGraphics gfx, string value, Element el, LabelDef label)
    {
        var ecc = el.Ecc switch
        {
            "L" => ErrorCorrectionLevel.L,
            "Q" => ErrorCorrectionLevel.Q,
            "H" => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M
        };
        var qr = Encoder.encode(value, ecc);
        var m = qr.Matrix;
        var n = m.Width;                       // so o, chua tinh quiet zone
        _lint.Qr(label.Id, value, n, el.Size);

        var cell = el.Size / (n + 8);          // quiet zone 4 o moi ben
        var off = 4 * cell;
        for (var r = 0; r < n; r++)
            for (var c = 0; c < n; c++)
                if (m[c, r] == 1)
                    gfx.DrawRectangle(XBrushes.Black,
                        Mm(el.X + off + c * cell), Mm(el.Y + off + r * cell),
                        Mm(cell) + 0.05, Mm(cell) + 0.05);   // +0.05 chong ho vien
    }
}
