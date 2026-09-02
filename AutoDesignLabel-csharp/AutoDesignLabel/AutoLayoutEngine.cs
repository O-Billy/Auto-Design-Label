using System.Text.RegularExpressions;
using ZXing.OneD;

namespace AutoDesignLabel;

/// <summary>
/// Bien "content spec" (danh sach field + kich thuoc nhan) thanh LabelDef co toa do - dung khi PMD
/// khong co toa do vector (content-only). Font/loai barcode/vi tri deu la MAC DINH: PMD dang nay
/// khong quy dinh chung, nen ket qua danh dau LayoutSource=Auto de buoc review.
///
/// Archetype ho tro: "simple-id" (nhan ID nho), "packing" (nhan dong goi/xuat xu lon),
/// "lit-kit" (nhan danh sach kit: barcode serial + bang). "unknown" -> chi 1 dong ghi chu.
///
/// Bai hoc tu prototype (tranh lap lai):
///   - Ngan sach chieu cao dong tinh CO DINH, KHONG dung max(bc_h, min) kieu ti le - se lam chu
///     de len barcode tren nhan nho.
///   - Quiet zone tu quan (>=10x X-dimension, toi thieu 1mm), KHONG de thu vien tu ap 0.25 inch.
///   - HRI (dong chu nguoi doc) luon DUOI dai vach, co khoang cach - khong de len barcode.
/// </summary>
public static class AutoLayoutEngine
{
    private const double MarginX = 1.5;
    private const double MarginTop = 1.2;
    private const double MarginBottom = 1.0;
    private const double QuietZoneMm = 1.0;      // >= 10 x X-dim toi thieu (0.19mm) va du rong
    private const double RowGapMm = 0.5;
    private const string NeutralFont = JioTypeFontResolver.NeutralFamilyName;

    public static LdmDocument BuildDocument(ContentSpec spec, IReadOnlyDictionary<string, string>? data = null)
    {
        var doc = new LdmDocument
        {
            DocumentId = spec.DocumentId,
            Revision = spec.Revision,
            SourcePmd = spec.SourcePdf,
        };

        var issueNo = 0;
        foreach (var cl in spec.Labels)
        {
            var label = Build(cl, data, out var issues);
            doc.Labels.Add(label);
            foreach (var (severity, text) in issues)
                doc.OpenIssues.Add(new OpenIssue
                {
                    Ref = $"auto-{++issueNo}",
                    Severity = severity,
                    Text = $"[{label.Id}] {text}",
                });

        }

        // Fields block dung chung ca tai lieu: voi moi token, lay gia tri mau TIN CAY NHAT trong
        // bat ky nhan nao (nhan A co the giai ma duoc ma vach ma nhan B thi khong).
        foreach (var g in spec.Labels
                     .SelectMany(cl => cl.Fields.Where(f => f.Kind != ContentRenderKind.Table).Select(f => (cl, f)))
                     .Where(x => !IsBakedPartNo(x.f, x.cl))
                     .GroupBy(x => x.f.Token))
        {
            var best = g.OrderByDescending(x => string.IsNullOrEmpty(EffectiveSample(x.f, x.cl, data)) ? -1 : x.f.Confidence)
                        .First();
            doc.Fields[g.Key] = new FieldDef
            {
                Source = best.f.Source == "barcode" ? "barcode" : "unknown",
                Sample = EffectiveSample(best.f, best.cl, data) ?? "",
                Note = best.f.Caption,
            };
        }

        foreach (var note in spec.Notes)
            doc.OpenIssues.Add(new OpenIssue { Ref = $"auto-{++issueNo}", Severity = "major", Text = note });

        return doc;
    }

    private static readonly Regex TokenRx = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    /// <summary>Dam bao moi {{TOKEN}} trong element deu co gia tri de render PROOF - LDM van giu
    /// token that (template tai su dung duoc). Thieu -> lay Sample tu Fields, hoac placeholder
    /// "&lt;TOKEN&gt;" de nhin thay ngay tren PDF. Tra ve danh sach token phai dung placeholder.
    /// Khong ghi de gia tri da co trong data.</summary>
    public static List<string> EnsureRenderData(LdmDocument doc, IDictionary<string, string> data)
    {
        var placeholders = new List<string>();
        foreach (var el in doc.Labels.SelectMany(l => l.Elements))
        foreach (var s in new[] { el.Text, el.Data })
        {
            if (s is null) continue;
            foreach (Match m in TokenRx.Matches(s))
            {
                var key = m.Groups[1].Value;
                if (data.ContainsKey(key)) continue;
                var sample = doc.Fields.TryGetValue(key, out var fd) && !string.IsNullOrWhiteSpace(fd.Sample)
                    ? fd.Sample! : $"<{key}>";
                data[key] = sample;
                if (sample.StartsWith('<')) placeholders.Add(key);
            }
        }
        return placeholders.Distinct().ToList();
    }

    public static LabelDef Build(ContentLabel cl, IReadOnlyDictionary<string, string>? data,
        out List<(string Severity, string Text)> issues)
    {
        issues = new();
        var label = new LabelDef
        {
            Id = cl.Id,
            Name = cl.Name,
            PartNumber = cl.Product,
            WidthMm = cl.WidthMm,
            HeightMm = cl.HeightMm,
            Quantity = 1,
            LayoutSource = LayoutSource.Auto,
            RequiresAutoDesign = true,
            LayoutConfidence = "Auto-layout tu content spec (PMD content-only) - can review thiet ke",
        };
        foreach (var note in cl.Notes) issues.Add(("major", note));

        if (cl.WidthMm <= 0 || cl.HeightMm <= 0)
        {
            issues.Add(("blocker", "Chua co kich thuoc nhan - khong the sinh layout."));
            return label;
        }

        switch (cl.Archetype)
        {
            case "simple-id": LayoutSimpleId(label, cl, data, issues); break;
            case "packing":   LayoutPacking(label, cl, data, issues); break;
            case "lit-kit":   LayoutLitKit(label, cl, data, issues); break;
            default:
                issues.Add(("major",
                    $"Archetype '{cl.Archetype}' chua duoc ho tro - nhan chua co layout, can dat thu cong."));
                label.Elements.Add(CenterNote(cl, $"[{cl.Archetype}] chua co layout tu dong"));
                break;
        }

        return label;
    }

    // ==================================================================
    // Helpers dung chung
    // ==================================================================

    private readonly record struct FieldValue(ContentField Field, string Expr, string BarcodeExpr, string? Sample, bool Baked)
    {
        /// <summary>Chuoi thuc su ENCODE trong ma vach (kem data identifier neu co) - de uoc luong so module.</summary>
        public string? EncodedSample => Sample is null ? null : (Field.DataIdentifier ?? "") + Sample;
    }

    private static FieldValue Resolve(ContentField f, ContentLabel cl, IReadOnlyDictionary<string, string>? data)
    {
        var baked = IsBakedPartNo(f, cl);
        var expr = baked ? cl.Product : "{{" + f.Token + "}}";
        var barcodeExpr = string.IsNullOrEmpty(f.DataIdentifier) ? expr : f.DataIdentifier + expr;
        var sample = (baked ? cl.Product : null) ?? EffectiveSample(f, cl, data);
        return new FieldValue(f, expr, barcodeExpr, sample, baked);
    }

    /// <summary>Gia tri mau uu tien: OCR tin cay thap KHONG duoc de len gia tri tu data JSON
    /// (OCR anh nhan PPT hay doc nham so). Thu tu: data JSON -> OCR/pdfText sample -> null.</summary>
    private static string? EffectiveSample(ContentField f, ContentLabel cl, IReadOnlyDictionary<string, string>? data)
    {
        var dataVal = data is not null && data.TryGetValue(f.Token, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var lowConfOcr = f.Source == "ocr" && f.Confidence < 0.5;
        return lowConfOcr ? (dataVal ?? f.SampleValue) : (f.SampleValue ?? dataVal);
    }

    private static bool IsBakedPartNo(ContentField f, ContentLabel cl) =>
        f.Token is "PART_NUMBER" or "KIT_PN"
        && !string.IsNullOrWhiteSpace(cl.Product)
        && cl.Product.Any(char.IsLetterOrDigit);

    /// <summary>Ve 1 khoi barcode trong dai [top, top+blockH]: dong caption (co the kem gia tri),
    /// dai vach, va (neu withHri) 1 dong HRI ben duoi. Tra ve issue X-dimension / UPC neu can.</summary>
    private static void EmitBarcodeBlock(LabelDef label, FieldValue fv, double x, double top,
        double blockW, double blockH, double textPt, bool withHri, List<(string, string)> issues)
    {
        var ascMm = textPt * 0.72 / 2.835;
        var lineMm = textPt / 72.0 * 25.4 * 1.15;
        var captionText = withHri ? $"{fv.Field.Caption}:" : $"{fv.Field.Caption}: {fv.Expr}";

        label.Elements.Add(new Element
        {
            Type = "text", X = x, Y = Math.Round(top + ascMm, 2),
            Size = textPt, Font = "medium", FontFamily = NeutralFont, Align = "left",
            Text = captionText,
        });

        var hriMm = withHri ? lineMm + RowGapMm : 0;
        var bcTop = top + lineMm + RowGapMm;
        var bcBottom = top + blockH - hriMm - RowGapMm;
        var bcH = Math.Round(Math.Max(bcBottom - bcTop, 1.5), 2);
        var bcW = Math.Round(blockW - 2 * QuietZoneMm, 2);

        label.Elements.Add(new Element
        {
            Type = "barcode128", X = Math.Round(x + QuietZoneMm, 2), Y = Math.Round(bcTop, 2),
            Width = bcW, Height = bcH, Data = fv.BarcodeExpr,
        });

        if (withHri)
            label.Elements.Add(new Element
            {
                Type = "text", X = x, Y = Math.Round(bcTop + bcH + ascMm + RowGapMm, 2),
                Size = textPt, Font = "light", FontFamily = NeutralFont, Align = "left",
                Text = fv.Expr,
            });

        if (fv.EncodedSample is { Length: > 0 })
        {
            var modules = new Code128Writer().encode(fv.EncodedSample).Length;
            var xDim = bcW / modules;
            if (xDim < Linter.MinXDimMm)
                issues.Add(("major",
                    $"Barcode '{fv.Field.Caption}' (\"{fv.Sample}\", {modules} module): X-dimension ~{xDim:F3}mm " +
                    $"< {Linter.MinXDimMm}mm o be rong {bcW:F1}mm. Rut ngan noi dung ma hoa hoac tang kich thuoc nhan."));
        }
        else
        {
            issues.Add(("minor",
                $"Barcode '{fv.Field.Caption}': chua co gia tri mau de uoc luong X-dimension - xem lint report."));
        }

        if (fv.Field.Token is "UPC" or "EAN" && fv.Sample is { Length: >= 12 } s && s.All(char.IsDigit))
            issues.Add(("major",
                $"Field '{fv.Field.Caption}': artwork da duyet dung ma UPC-A/EAN; hien render Code128. " +
                "Xac nhan voi PM neu can symbology that."));

        if (fv.Baked)
            issues.Add(("minor", $"Part number '{label.PartNumber}' lay tu tieu de slide - xac nhan lai."));
    }

    private static Element CenterNote(ContentLabel cl, string text) => new()
    {
        Type = "text",
        X = Math.Round(cl.WidthMm / 2, 2),
        Y = Math.Round(cl.HeightMm / 2, 2),
        Size = Math.Clamp(cl.HeightMm * 0.12, 4.0, 9.0),
        Font = "light", FontFamily = NeutralFont, Align = "center",
        Text = text,
    };

    private static double FitTextPt(double startPt, double minPt, int maxChars, double availMm)
    {
        var pt = startPt;
        while (pt > minPt && maxChars * pt * 0.55 / 2.835 > availMm) pt -= 0.5;
        return pt;
    }

    // ==================================================================
    // simple-id: nhan ID nho - moi field barcode = 1 dong "Caption: value" + barcode, xep doc.
    // Khong tach HRI rieng (khong du cho); gia tri nam ngay tren dong caption.
    // ==================================================================

    private static void LayoutSimpleId(LabelDef label, ContentLabel cl,
        IReadOnlyDictionary<string, string>? data, List<(string, string)> issues)
    {
        var fields = cl.Fields.Where(f => f.Kind == ContentRenderKind.Barcode).ToList();
        foreach (var f in cl.Fields.Where(f => f.Kind == ContentRenderKind.Text))
            issues.Add(("minor", $"Field '{f.Caption}' (text) chua duoc dat vao layout simple-id."));

        if (fields.Count == 0)
        {
            issues.Add(("major", "Khong co field barcode nao - khong sinh duoc layout simple-id."));
            label.Elements.Add(CenterNote(cl, "simple-id: khong co field barcode"));
            return;
        }

        double w = cl.WidthMm, h = cl.HeightMm;
        if (w - 2 * MarginX - 2 * QuietZoneMm < 5)
        {
            issues.Add(("blocker", $"Nhan qua hep ({w}mm) cho barcode + quiet zone."));
            return;
        }

        var rowH = (h - MarginTop - MarginBottom) / fields.Count;
        var rows = fields.Select(f => Resolve(f, cl, data)).ToList();
        var maxChars = rows.Max(r => $"{r.Field.Caption}: {r.Sample ?? r.Expr}".Length);
        var textPt = FitTextPt(Math.Clamp(Math.Round(rowH * 0.30 * 72.0 / 25.4, 1), 3.5, 8.0),
                               3.5, maxChars, w - 2 * MarginX);

        for (var i = 0; i < rows.Count; i++)
            EmitBarcodeBlock(label, rows[i], MarginX, MarginTop + i * rowH,
                w - 2 * MarginX, rowH, textPt, withHri: false, issues);
    }

    // ==================================================================
    // packing: nhan lon (vd 102x62). Field barcode xep doc o phan tren (co HRI rieng); field text
    // (Description, Made in...) thanh cac dong o phan duoi.
    // ==================================================================

    private static void LayoutPacking(LabelDef label, ContentLabel cl,
        IReadOnlyDictionary<string, string>? data, List<(string, string)> issues)
    {
        double w = cl.WidthMm, h = cl.HeightMm;
        var barcodeFields = cl.Fields.Where(f => f.Kind == ContentRenderKind.Barcode).Select(f => Resolve(f, cl, data)).ToList();
        var textFields = cl.Fields.Where(f => f.Kind == ContentRenderKind.Text).Select(f => Resolve(f, cl, data)).ToList();

        if (barcodeFields.Count == 0 && textFields.Count == 0)
        {
            issues.Add(("major", "Khong co field nao - khong sinh duoc layout packing."));
            label.Elements.Add(CenterNote(cl, "packing: khong co field"));
            return;
        }

        var textPt = Math.Clamp(Math.Round(h * 0.05 * 72.0 / 25.4, 1), 5.0, 9.0);
        var lineMm = textPt / 72.0 * 25.4 * 1.15;
        var ascMm = textPt * 0.72 / 2.835;

        // Chia chieu cao: moi barcode block ~ 3 dong (caption + vach + HRI); moi text field ~ 1.4 dong.
        var contentH = h - MarginTop - MarginBottom;
        var barcodeUnits = barcodeFields.Count * (3 * lineMm + 2 * RowGapMm + 3.0);   // +3mm vach toi thieu
        var textUnits = textFields.Count * lineMm * 1.6;
        var scale = barcodeUnits + textUnits > contentH && barcodeUnits + textUnits > 0
            ? contentH / (barcodeUnits + textUnits) : 1.0;

        var y = MarginTop;
        foreach (var fv in barcodeFields)
        {
            var blockH = (3 * lineMm + 2 * RowGapMm + 3.0) * scale;
            EmitBarcodeBlock(label, fv, MarginX, y, w - 2 * MarginX, blockH, textPt, withHri: true, issues);
            y += blockH + RowGapMm;
        }

        foreach (var fv in textFields)
        {
            label.Elements.Add(new Element
            {
                Type = "text", X = MarginX, Y = Math.Round(y + ascMm, 2),
                Size = textPt, Font = "light", FontFamily = NeutralFont, Align = "left",
                Text = $"{fv.Field.Caption}: {fv.Expr}",
            });
            y += lineMm * 1.6 * scale;
        }

        if (y > h + 0.5)
            issues.Add(("major", $"Noi dung packing (~{y:F0}mm) vuot chieu cao nhan {h}mm - can rut gon / dat thu cong."));
    }

    // ==================================================================
    // lit-kit: barcode serial o dau + bang danh sach kit (Part Number | Serial Number).
    // Bang duoc BAKE thanh cac dong text tinh tu data (KIT_COUNT / KIT_PN{i} / KIT_SN{i}) - CS6
    // khong bind duoc nhieu bien vao 1 o bang nen dot nay khong dung element 'repeat'.
    // ==================================================================

    private static void LayoutLitKit(LabelDef label, ContentLabel cl,
        IReadOnlyDictionary<string, string>? data, List<(string, string)> issues)
    {
        double w = cl.WidthMm, h = cl.HeightMm;
        var serial = cl.Fields.FirstOrDefault(f => f.Token == "SERIAL");
        var textPt = Math.Clamp(Math.Round(h * 0.045 * 72.0 / 25.4, 1), 4.5, 8.0);
        var lineMm = textPt / 72.0 * 25.4 * 1.15;
        var ascMm = textPt * 0.72 / 2.835;

        var y = MarginTop;

        // Header: product + serial barcode block
        label.Elements.Add(new Element
        {
            Type = "text", X = MarginX, Y = Math.Round(y + ascMm, 2),
            Size = textPt, Font = "medium", FontFamily = NeutralFont, Align = "left",
            Text = cl.Product,
        });
        y += lineMm + RowGapMm;

        if (serial is not null)
        {
            var fv = Resolve(serial, cl, data);
            var blockH = 2 * lineMm + 2 * RowGapMm + 4.0;
            EmitBarcodeBlock(label, fv, MarginX, y, w - 2 * MarginX, blockH, textPt, withHri: true, issues);
            y += blockH + RowGapMm * 2;
        }

        // Bang kit
        var col1 = MarginX;
        var col2 = MarginX + Math.Min(28.0, w * 0.35);
        var col3 = MarginX + Math.Min(28.0, w * 0.35) + Math.Min(38.0, w * 0.4);

        void Row(string a, string b, string c, string font, double yy)
        {
            label.Elements.Add(new Element { Type = "text", X = col1, Y = Math.Round(yy, 2), Size = textPt, Font = font, FontFamily = NeutralFont, Align = "left", Text = a });
            label.Elements.Add(new Element { Type = "text", X = Math.Round(col2, 2), Y = Math.Round(yy, 2), Size = textPt, Font = font, FontFamily = NeutralFont, Align = "left", Text = b });
            label.Elements.Add(new Element { Type = "text", X = Math.Round(col3, 2), Y = Math.Round(yy, 2), Size = textPt, Font = font, FontFamily = NeutralFont, Align = "left", Text = c });
        }

        Row("SL", "Part Number", "Serial Number", "medium", y + ascMm);
        label.Elements.Add(new Element { Type = "line", X1 = MarginX, Y1 = Math.Round(y + lineMm, 2), X2 = Math.Round(w - MarginX, 2), Y2 = Math.Round(y + lineMm, 2) });
        y += lineMm + RowGapMm;

        var maxRows = (int)Math.Floor((h - MarginBottom - y) / lineMm);

        // Nguon dong bang kit, theo do tin cay: KitRows (da resolve tu doi chieu cheo) -> data JSON
        // KIT_COUNT/KIT_PN{i}/KIT_SN{i} -> vai dong {{token}} lam mau.
        var rows = new List<(string Pn, string Sn)>();
        if (cl.KitRows.Count > 0)
        {
            rows.AddRange(cl.KitRows.Select(r => (r.PartNumber, r.Serial)));
        }
        else if (data is not null && data.TryGetValue("KIT_COUNT", out var kc) && int.TryParse(kc, out var n) && n > 0)
        {
            for (var i = 1; i <= n; i++)
                rows.Add((data.TryGetValue($"KIT_PN{i}", out var p) ? p : $"{{{{KIT_PN{i}}}}}",
                          data.TryGetValue($"KIT_SN{i}", out var s) ? s : $"{{{{KIT_SN{i}}}}}"));
        }
        else
        {
            issues.Add(("major", "Bang kit: chua co du lieu - hien vai dong mau."));
            for (var i = 1; i <= Math.Min(5, Math.Max(0, maxRows)); i++)
                rows.Add(($"{{{{KIT_PN{i}}}}}", $"{{{{KIT_SN{i}}}}}"));
        }

        if (rows.Count > maxRows)
            issues.Add(("major", $"Bang kit co {rows.Count} dong nhung nhan chi chua ~{maxRows} - can nhan thu 2 / thu nho font."));
        for (var i = 0; i < Math.Min(rows.Count, Math.Max(maxRows, 0)); i++)
        {
            Row((i + 1).ToString(), rows[i].Pn, rows[i].Sn, "light", y + ascMm);
            y += lineMm;
        }
    }
}
