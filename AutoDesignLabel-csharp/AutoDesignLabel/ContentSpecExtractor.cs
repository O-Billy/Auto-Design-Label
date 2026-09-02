using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AutoDesignLabel;

/// <summary>
/// Trich "content spec" tu PMD dang content-only (PPT -> PDF). Nguon du lieu, theo do tin cay:
///   1. GIAI MA MA VACH trong anh nhan (ZXing) - gia tri co checksum -> chinh xac. Part number /
///      serial / UPC deu nam trong ma vach nen day la nguon auto-fill CHINH.
///   2. Cau truc serial mo ta trong lop text ('"0"-Factory code', '"A"-...' ...) -> dung de sua
///      ket qua OCR va lam mau serial.
///   3. Doi chieu CHEO giua cac nhan (accessory ID labels <-> bang kit trong LIT KIT label).
///   4. OCR chu (HRI / mo ta / xuat xu) + sua nham chu-so - chi khi 1-3 khong ra.
///
/// Lop text luon cho: tieu de slide (-> id/ten/part number), "LABEL SIZE", callout (-> field list).
/// </summary>
public sealed class ContentSpecExtractor
{
    private readonly IPmdImageReader _reader;

    public ContentSpecExtractor(IPmdImageReader? reader = null) => _reader = reader ?? new NullImageReader();

    private sealed record LabelImages(List<DecodedBarcode> Barcodes, List<string> OcrLines);

    public ContentSpec Extract(byte[] pdfBytes, string sourceFileName)
    {
        using var pdf = PdfDocument.Open(pdfBytes);
        var spec = new ContentSpec { SourcePdf = sourceFileName };
        var images = new Dictionary<string, LabelImages>();
        var allLines = new List<string>();

        for (var n = 1; n <= pdf.NumberOfPages; n++)
        {
            var page = pdf.GetPage(n);
            var lines = BuildLines(page);
            if (lines.Count == 0) continue;
            allLines.AddRange(lines.Select(l => l.Text));

            var title = FindTitle(lines, page);
            if (title is null) continue;                    // slide bia / slide "The End" - bo qua

            var (w, h) = ParseLabelSize(lines);
            var label = new ContentLabel
            {
                Id = Slug(title.Text),
                Name = title.Text,
                Product = ParseProduct(title.Text),
                WidthMm = w,
                HeightMm = h,
            };
            CollectFields(lines, label);

            var img = ReadImages(page, label.Id);
            images[label.Id] = img;
            foreach (var b in img.Barcodes) label.OcrRawLines.Add($"[{b.Format}] {b.Value}");
            label.OcrRawLines.AddRange(img.OcrLines);

            if (w <= 0 && label.Fields.Count == 0) continue;
            if (w <= 0 || h <= 0)
                label.Notes.Add("Khong doc duoc 'LABEL SIZE' - nhap kich thuoc nhan thu cong.");
            if (label.Fields.Count == 0)
                label.Notes.Add("Khong nhan dien duoc field nao tu callout - kiem tra lai PMD / nhap thu cong.");

            spec.Labels.Add(label);
        }

        var serialStruct = ParseSerialStructure(allLines);

        foreach (var label in spec.Labels)
            FillFromImages(label, images.GetValueOrDefault(label.Id), serialStruct);

        ResolveSerialConsensus(spec, serialStruct);
        ResolveKitTable(spec, images);

        foreach (var label in spec.Labels)
            label.Archetype = GuessArchetype(label);

        spec.DocumentId = spec.Labels.FirstOrDefault()?.Product is { Length: > 0 } p ? p : "UNKNOWN";
        if (spec.Labels.Count == 0)
            spec.Notes.Add("Khong tim thay nhan nao trong PDF (khong co slide nao co tieu de dang '<PN> ... LABEL').");
        return spec;
    }

    // ------------------------------------------------------------------
    // Gom Word thanh dong theo baseline Y (don gian - du cho lop text PPT phang, khong chong lop)
    // ------------------------------------------------------------------

    private sealed record Line(string Text, double Left, double Right, double BaselineY, double Top, double PointSize);

    private static List<Line> BuildLines(Page page)
    {
        var words = page.GetWords()
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .OrderByDescending(w => Math.Round(w.BoundingBox.Bottom, 0))
            .ThenBy(w => w.BoundingBox.Left)
            .ToList();

        var lines = new List<Line>();
        List<UglyToad.PdfPig.Content.Word> cur = new();

        void Flush()
        {
            if (cur.Count == 0) return;
            var text = string.Join(" ", cur.Select(w => w.Text)).Trim();
            var size = cur.SelectMany(w => w.Letters).Select(l => l.PointSize).DefaultIfEmpty(0).Max();
            lines.Add(new Line(
                text,
                cur.Min(w => w.BoundingBox.Left),
                cur.Max(w => w.BoundingBox.Right),
                cur[0].BoundingBox.Bottom,
                cur.Max(w => w.BoundingBox.Top),
                size));
            cur.Clear();
        }

        double? lastY = null;
        foreach (var w in words)
        {
            if (lastY.HasValue && Math.Abs(w.BoundingBox.Bottom - lastY.Value) > 3) Flush();
            cur.Add(w);
            lastY = w.BoundingBox.Bottom;
        }
        Flush();
        return lines;
    }

    // ------------------------------------------------------------------
    // Tieu de slide = dong font lon nhat, nam tren cao, khong phai footer "Confidential"
    // ------------------------------------------------------------------

    private static readonly Regex TitleLikeRx =
        new(@"label|origin|packing|kit", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static Line? FindTitle(List<Line> lines, Page page)
    {
        var maxSize = lines.Max(l => l.PointSize);
        var candidates = lines
            .Where(l => l.PointSize >= maxSize - 2.0)
            .Where(l => !l.Text.Contains("Confidential", StringComparison.OrdinalIgnoreCase))
            .Where(l => l.Top >= page.Height * 0.55)          // nua tren trang
            .Where(l => Regex.IsMatch(l.Text, "[A-Za-z]"))
            .OrderByDescending(l => l.Top)
            .ToList();

        return candidates.FirstOrDefault(l => TitleLikeRx.IsMatch(l.Text)) ?? candidates.FirstOrDefault();
    }

    // Tieu de dang "<PART NUMBER>-<KIND>", part number CO THE co dau '-' ("0M-99987", "0M-7770B").
    // Cach chac chan: cat bo cum "kind" o CUOI, phan con lai (bo dau '-' thua) la part number.
    private static readonly Regex KindSuffixRx = new(
        @"[-–—\s]*(Accessory\s+ID\s+LABEL|LIT\s+KIT\s+LABEL|ID\s+LABEL|Packing\s+Label|Origin\s+Label|LABEL)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string ParseProduct(string title)
    {
        var m = KindSuffixRx.Match(title);
        var head = (m.Success ? title[..m.Index] : title).Trim().Trim('-', '–', '—').Trim();
        if (head.Length > 0 && head.Length <= 20 && !head.Contains(',')) return head;
        return title.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    // ------------------------------------------------------------------
    // LABEL SIZE
    // ------------------------------------------------------------------

    private static (double W, double H) ParseLabelSize(List<Line> lines)
    {
        foreach (var l in lines)
        {
            var m = Regex.Match(l.Text,
                @"LABEL\s*SIZE\s*[:：]?\s*(\d+(?:\.\d+)?)\s*[xX*×]\s*(\d+(?:\.\d+)?)\s*MM",
                RegexOptions.IgnoreCase);
            if (m.Success)
                return (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value));

            var d = Regex.Match(l.Text, @"LABEL\s*SIZE\s*[:：]?\s*D\s*(\d+(?:\.\d+)?)\s*MM", RegexOptions.IgnoreCase);
            if (d.Success)
            {
                var dia = double.Parse(d.Groups[1].Value);
                return (dia, dia);
            }
        }
        return (0, 0);
    }

    // ------------------------------------------------------------------
    // Callout -> field. Thu tu quan trong: "KIT assembly part number" phai xet TRUOC "part number".
    // ------------------------------------------------------------------

    private sealed record FieldRule(Regex Pattern, string Token, string Caption, ContentRenderKind Kind);

    private static readonly FieldRule[] Rules =
    {
        new(new(@"list\s+of\s+kit|kit.*used\s+in\s+project", RegexOptions.IgnoreCase), "KIT_TABLE", "Kit list", ContentRenderKind.Table),
        new(new(@"kit\s*assembly\s*part\s*(number|no)", RegexOptions.IgnoreCase),       "KIT_PN",    "Part No.",   ContentRenderKind.Barcode),
        new(new(@"part\s*(number|no)\b", RegexOptions.IgnoreCase),                      "PART_NUMBER","Part No.",   ContentRenderKind.Barcode),
        new(new(@"serial\s*(number|no)\b", RegexOptions.IgnoreCase),                    "SERIAL",    "Serial No.", ContentRenderKind.Barcode),
        new(new(@"product\s*description|^\s*description\b", RegexOptions.IgnoreCase),   "DESCRIPTION","Description",ContentRenderKind.Text),
        new(new(@"\bUPC\b", RegexOptions.IgnoreCase),                                   "UPC",       "UPC",        ContentRenderKind.Barcode),
        new(new(@"product\s*qty|\bqty\b|\bquantity\b", RegexOptions.IgnoreCase),        "QTY",       "Qty",        ContentRenderKind.Barcode),
        new(new(@"made\s*in|country\s*of\s*origin", RegexOptions.IgnoreCase),           "COUNTRY",   "Made in",    ContentRenderKind.Text),
    };

    private static readonly string[] TokenOrder =
        { "PART_NUMBER", "KIT_PN", "SERIAL", "DESCRIPTION", "COUNTRY", "UPC", "QTY", "KIT_TABLE" };

    private static void CollectFields(List<Line> lines, ContentLabel label)
    {
        // Callout trong o text co the bi xuong dong ("KIT assembly part" / "number") - thu khop tren
        // tung dong RIENG va tren dong ghep voi 1-2 dong ke tiep.
        var probes = new List<string>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Text.Length is 0 or > 80) continue;
            probes.Add(lines[i].Text);
            if (i + 1 < lines.Count && lines[i + 1].Text.Length <= 40)
                probes.Add(lines[i].Text + " " + lines[i + 1].Text);
            if (i + 2 < lines.Count && lines[i + 1].Text.Length <= 40 && lines[i + 2].Text.Length <= 40)
                probes.Add(lines[i].Text + " " + lines[i + 1].Text + " " + lines[i + 2].Text);
        }

        var seen = new HashSet<string>();
        foreach (var probe in probes)
        {
            if (probe.Length > 80) continue;
            foreach (var rule in Rules)
            {
                if (!rule.Pattern.IsMatch(probe)) continue;
                if (!seen.Add(rule.Token)) break;
                label.Fields.Add(new ContentField
                {
                    Token = rule.Token,
                    Caption = rule.Caption,
                    Kind = rule.Kind,
                    Source = "pdfText",
                    Confidence = 0.6,
                });
                break;      // 1 probe khop 1 rule
            }
        }

        label.Fields.Sort(FieldOrder);
    }

    // ==================================================================
    // Doc anh nhan: ma vach (chinh xac) + OCR (du phong)
    // ==================================================================

    private static readonly Regex SerialCleanRx = new(@"^[0-9][A-Z0-9]{8,15}$", RegexOptions.Compiled);
    private static readonly Regex DigitsRx = new(@"^[0-9]{10,14}$", RegexOptions.Compiled);
    private static readonly Regex CodeLikeRx = new(@"^[A-Za-z0-9][A-Za-z0-9 .\-/]{2,}$", RegexOptions.Compiled);

    private LabelImages ReadImages(Page page, string labelId)
    {
        var debug = Environment.GetEnvironmentVariable("ADL_OCR_DEBUG") == "1";
        var barcodes = new List<DecodedBarcode>();
        var ocr = new List<string>();
        try
        {
            foreach (var im in page.GetImages())
            {
                byte[]? bytes = im.TryGetPng(out var png) && png is { Length: > 0 }
                    ? png
                    : im.RawMemory.Length > 0 ? im.RawMemory.ToArray() : null;
                if (bytes is null) continue;
                try { barcodes.AddRange(_reader.Barcodes(bytes)); } catch { }
                try { ocr.AddRange(_reader.Ocr(bytes).Select(l => l.Text.Trim()).Where(t => t.Length > 0)); } catch { }
            }
        }
        catch (Exception ex) { if (debug) Console.Error.WriteLine($"[img {labelId}] {ex.Message}"); }

        barcodes = barcodes.DistinctBy(b => b.Format + "|" + b.Value).ToList();
        ocr = ocr.Distinct().ToList();
        if (debug)
        {
            foreach (var b in barcodes) Console.Error.WriteLine($"[bc {labelId}] {b.Format}: {b.Value}");
            foreach (var l in ocr) Console.Error.WriteLine($"[ocr {labelId}] \"{l}\"");
        }
        return new LabelImages(barcodes, ocr);
    }

    // ------------------------------------------------------------------
    // Dien gia tri mau tu ma vach (uu tien) roi OCR (du phong)
    // ------------------------------------------------------------------

    // ANSI MH10.8.2 data identifier -> token. Tach tien to khoi gia tri ma vach.
    private static readonly (string Di, string Token)[] DataIdentifiers =
    {
        ("30P", "PART_NUMBER"), ("2P", "PART_NUMBER"), ("1P", "PART_NUMBER"), ("P", "PART_NUMBER"),
        ("25S", "SERIAL"), ("S", "SERIAL"),
        ("Q", "QTY"),
    };

    private static (string? Di, string Value, string? Token) SplitDataIdentifier(string raw)
    {
        foreach (var (di, token) in DataIdentifiers)
            if (raw.StartsWith(di, StringComparison.Ordinal) && raw.Length - di.Length >= 3)
                return (di, raw[di.Length..], token);
        return (null, raw, null);
    }

    private static void FillFromImages(ContentLabel label, LabelImages? img, SerialStructure? serialStruct)
    {
        if (img is null) return;

        var retail = img.Barcodes.Where(b => b.Format is "UPC_A" or "EAN_13" or "EAN_8").ToList();
        var code128 = img.Barcodes
            .Where(b => b.Format is "CODE_128" or "CODE_39")
            .Select(b => { var (di, val, tok) = SplitDataIdentifier(b.Value); return (Raw: b.Value, Di: di, Value: val, Token: tok); })
            .ToList();

        ContentField? Field(params string[] toks) => label.Fields.FirstOrDefault(f => toks.Contains(f.Token));

        // 1) Ma vach co data identifier -> gan thang vao field tuong ung (chinh xac nhat).
        foreach (var bc in code128.Where(b => b.Token is not null).ToList())
        {
            var f = bc.Token == "PART_NUMBER" ? Field("PART_NUMBER", "KIT_PN") : Field(bc.Token!);
            if (f is null) continue;
            Set(f, bc.Value.ToUpperInvariant(), "barcode", 0.97f);
            f.DataIdentifier = bc.Di;
            code128.Remove(bc);

            if (bc.Token == "PART_NUMBER" && !string.IsNullOrWhiteSpace(label.Product)
                && !string.Equals(bc.Value, label.Product, StringComparison.OrdinalIgnoreCase)
                && Ratio(NormLoose(label.Product), NormLoose(bc.Value)) >= 0.6)
            {
                label.Notes.Add($"Part number sua tu ma vach: '{label.Product}' (tieu de) -> '{bc.Value}' (ma vach).");
                label.Product = bc.Value.ToUpperInvariant();
            }
        }

        // 2) UPC / EAN
        var upcField = Field("UPC", "EAN");
        if (upcField is not null && retail.Count > 0)
        {
            Set(upcField, retail[0].Value, "barcode", 0.97f);
            label.Notes.Add($"UPC/EAN doc tu ma vach ({retail[0].Format}): {retail[0].Value}.");
        }

        // 3) Ma vach con lai (khong DI) -> khop theo dang
        var pnField = Field("PART_NUMBER", "KIT_PN");
        if (pnField is not null && string.IsNullOrEmpty(pnField.SampleValue) && !string.IsNullOrWhiteSpace(label.Product))
        {
            var hit = code128.FirstOrDefault(b => Ratio(NormLoose(label.Product), NormLoose(b.Value)) >= 0.7);
            if (hit.Raw is not null)
            {
                Set(pnField, hit.Value.ToUpperInvariant(), "barcode", 0.9f);
                if (!string.Equals(hit.Value, label.Product, StringComparison.OrdinalIgnoreCase))
                    label.Product = hit.Value.ToUpperInvariant();
                code128.Remove(hit);
            }
        }

        var serialField = Field("SERIAL");
        if (serialField is not null && string.IsNullOrEmpty(serialField.SampleValue))
        {
            var rx = serialStruct?.Pattern ?? SerialCleanRx;
            var hit = code128.FirstOrDefault(b => rx.IsMatch(b.Value.ToUpperInvariant()));
            if (hit.Raw is null) hit = code128.FirstOrDefault(b => SerialCleanRx.IsMatch(b.Value.ToUpperInvariant()));
            if (hit.Raw is not null) { Set(serialField, hit.Value.ToUpperInvariant(), "barcode", 0.95f); code128.Remove(hit); }
        }

        if (upcField is not null && string.IsNullOrEmpty(upcField.SampleValue))
        {
            var digits = code128.FirstOrDefault(b => b.Value.All(char.IsDigit) && b.Value.Length is >= 11 and <= 13);
            if (digits.Raw is not null) { Set(upcField, digits.Value, "barcode", 0.9f); code128.Remove(digits); }
        }

        var qtyField = Field("QTY");
        if (qtyField is not null && string.IsNullOrEmpty(qtyField.SampleValue))
        {
            var q = code128.FirstOrDefault(b => b.Value.Length <= 4 && b.Value.All(char.IsDigit));
            if (q.Raw is not null) { Set(qtyField, q.Value, "barcode", 0.9f); code128.Remove(q); }
        }

        // 4) OCR du phong cho cac field con trong
        FillFromOcr(label, img.OcrLines, serialStruct);
    }

    // OCR hay tra ky tu la (ø € £ §...) thay cho 0/1 - doi ve dang [A-Za-z0-9] truoc khi loc/sua.
    private static string Prefold(string s) => new string(s.Select(c => c switch
    {
        'ø' or 'Ø' or '¤' or '©' or '®' or '°' => '0',
        '€' or '£' or '¦' or '|' or 'ł' => '1',
        _ => c,
    }).ToArray());

    private static void FillFromOcr(ContentLabel label, List<string> ocrLinesRaw, SerialStructure? serialStruct)
    {
        if (ocrLinesRaw.Count == 0) return;
        var ocrLines = ocrLinesRaw.Select(Prefold).ToList();

        var codeLines = ocrLines
            .Where(t => t.Length is >= 3 and <= 40 && CodeLikeRx.IsMatch(t))
            .Where(t => !Rules.Any(r => r.Pattern.IsMatch(t)))
            .ToList();

        foreach (var f in label.Fields.Where(f =>
                     f.Kind != ContentRenderKind.Table && string.IsNullOrEmpty(f.SampleValue)))
        {
            if (f.Token is "PART_NUMBER" or "KIT_PN")
            {
                // OCR co the sua loi go "O"/"0" trong tieu de PMD: neu 1 dong OCR (sau prefold) khop
                // LONG voi tieu de nhung khac o ky tu O/0-style -> uu tien ban OCR.
                if (!string.IsNullOrWhiteSpace(label.Product))
                {
                    var want = NormLoose(label.Product);
                    var better = codeLines.FirstOrDefault(t =>
                        !string.Equals(t, label.Product, StringComparison.OrdinalIgnoreCase)
                        && t.Length == label.Product.Length
                        && NormLoose(t) == want
                        && CountDigits(t) > CountDigits(label.Product));
                    if (better is not null)
                    {
                        label.Notes.Add($"Part number sua theo anh: '{label.Product}' (tieu de) -> '{better.ToUpperInvariant()}'.");
                        label.Product = better.ToUpperInvariant();
                    }
                    Set(f, label.Product, "title", 0.75f);
                }
                continue;
            }

            if (f.Token is "DESCRIPTION" or "COUNTRY")
            {
                var kv = ocrLines
                    .Select(l => Regex.Match(l, @"^\s*(?:[A-Za-z][\w .()/&-]{1,30})\s*[:：]\s*(\S.{2,60})$"))
                    .FirstOrDefault(m => m.Success);
                if (kv is not null) Set(f, kv.Groups[1].Value.Trim(), "ocr", 0.5f);
                else if (f.Token == "COUNTRY")
                {
                    var made = ocrLines.FirstOrDefault(l => Regex.IsMatch(l, @"made\s*in|india|china|vietnam", RegexOptions.IgnoreCase));
                    if (made is not null) Set(f, Regex.Replace(made, @"^.*made\s*in[:：]?\s*", "", RegexOptions.IgnoreCase).Trim(), "ocr", 0.5f);
                }
                continue;
            }

            // SERIAL / UPC / QTY: sua nham chu-so roi khop dang
            var target = f.Token switch
            {
                "SERIAL" => serialStruct?.Pattern ?? SerialCleanRx,
                "UPC" or "EAN" => DigitsRx,
                "QTY" => new Regex(@"^[0-9]{1,4}$"),
                _ => null,
            };
            if (target is null) continue;

            foreach (var raw in codeLines)
            {
                var repaired = RepairCode(raw, f.Token, serialStruct);
                if (!target.IsMatch(repaired)) continue;
                Set(f, repaired, "ocr", 0.4f);
                label.Notes.Add($"OCR (anh mo, do tin cay thap): '{f.Caption}' ~ '{repaired}' (doc tho '{raw}') - xac nhan.");
                break;
            }
        }
    }

    private static void Set(ContentField f, string value, string source, float confidence)
    {
        if (!string.IsNullOrEmpty(f.SampleValue) && f.Confidence >= confidence) return;
        f.SampleValue = value;
        f.Source = source;
        f.Confidence = confidence;
    }

    // ------------------------------------------------------------------
    // Cau truc serial mo ta trong lop text: '"0"-Factory code', '"A"-Factory code identifier', ...
    // ------------------------------------------------------------------

    private sealed record SerialStructure(string SamplePrefix, int TotalLen, Regex Pattern);

    private static readonly Regex SerialPartRx =
        new("[\"“”']([A-Za-z0-9]{1,6})[\"“”']\\s*[-–—]\\s*([A-Za-z].{2,40})", RegexOptions.Compiled);

    private static SerialStructure? ParseSerialStructure(List<string> lines)
    {
        var parts = new List<(string Val, string Role)>();
        foreach (var l in lines)
            foreach (Match m in SerialPartRx.Matches(l))
                parts.Add((m.Groups[1].Value, m.Groups[2].Value.ToLowerInvariant()));
        if (parts.Count < 4) return null;

        // Loai trung, giu thu tu xuat hien; ghep lai thanh serial mau.
        var seen = new HashSet<string>();
        var ordered = parts.Where(p => seen.Add(p.Val + "|" + p.Role)).ToList();
        var sample = string.Concat(ordered.Select(p => p.Val));
        if (sample.Length is < 6 or > 24) return null;

        // Regex tu cau truc: moi part la chu (giu nguyen) hoac so (\d{n}).
        var pat = string.Concat(ordered.Select(p =>
            p.Val.All(char.IsDigit) ? $"[0-9]{{{p.Val.Length}}}" : Regex.Escape(p.Val.ToUpperInvariant())));
        // Phan "unit number" (part cuoi, toan so) co the dai/ngan hon mau -> noi long.
        return new SerialStructure(sample, sample.Length, new Regex($"^{pat}$", RegexOptions.IgnoreCase));
    }

    // Sua nham chu <-> so cho chuoi OCR, ton trong cau truc field.
    private static readonly Dictionary<char, char> ToDigit = new()
    {
        ['O'] = '0', ['Q'] = '0', ['D'] = '0', ['C'] = '0', ['U'] = '0', ['E'] = '0', ['Ø'] = '0',
        ['I'] = '1', ['L'] = '1', ['|'] = '1', ['!'] = '1', ['F'] = '1', ['T'] = '1', ['€'] = '1', ['/'] = '1',
        ['Z'] = '2', ['A'] = '4', ['S'] = '5', ['G'] = '6', ['B'] = '8', ['R'] = '8', ['?'] = '7',
    };

    private static string RepairCode(string raw, string token, SerialStructure? serialStruct)
    {
        var s = new string(raw.ToUpperInvariant().Where(c => char.IsLetterOrDigit(c) || c == '-').ToArray());

        if (token is "UPC" or "EAN" or "QTY")
            return new string(s.Select(c => char.IsDigit(c) ? c : ToDigit.GetValueOrDefault(c, c)).ToArray())
                .Where(char.IsDigit).Aggregate("", (a, c) => a + c);

        if (token == "SERIAL" && serialStruct is not null && s.Length >= serialStruct.SamplePrefix.Length - 2)
        {
            // Sua theo tung vi tri: cho nao mau la chu -> giu chu; cho nao la so -> ep ve so.
            var chars = s.ToCharArray();
            var proto = serialStruct.SamplePrefix.ToUpperInvariant();
            for (var i = 0; i < chars.Length && i < proto.Length; i++)
            {
                if (char.IsDigit(proto[i]) && !char.IsDigit(chars[i]))
                    chars[i] = ToDigit.GetValueOrDefault(chars[i], chars[i]);
                else if (char.IsLetter(proto[i]))
                    chars[i] = proto[i];   // vi tri chu la HANG SO (factory/contract code)
            }
            var head = new string(chars);
            // Duoi (unit number) - ep ve so
            var tail = chars.Length > proto.Length
                ? new string(s[proto.Length..].Select(c => char.IsDigit(c) ? c : ToDigit.GetValueOrDefault(c, c)).ToArray())
                : "";
            return head[..Math.Min(head.Length, proto.Length)] + tail;
        }

        if (token == "SERIAL")
        {
            // Khong co cau truc: chi ep tu vi tri 2 tro di ve so neu ky tu dau la [0-9], thu 2 la chu.
            if (s.Length >= 8)
            {
                var arr = s.ToCharArray();
                arr[0] = char.IsDigit(arr[0]) ? arr[0] : ToDigit.GetValueOrDefault(arr[0], arr[0]);
                for (var i = 2; i < arr.Length; i++)
                    if (!char.IsLetter(arr[i]) || i >= 7)
                        arr[i] = char.IsDigit(arr[i]) ? arr[i] : ToDigit.GetValueOrDefault(arr[i], arr[i]);
                return new string(arr);
            }
        }
        return s;
    }

    // ------------------------------------------------------------------
    // Doi chieu cheo giua cac nhan
    // ------------------------------------------------------------------

    private static void ResolveSerialConsensus(ContentSpec spec, SerialStructure? serialStruct)
    {
        // Serial doc tu MA VACH la dang tin cay -> lay tien to chung ap cho nhan chi co serial OCR.
        var exact = spec.Labels
            .SelectMany(l => l.Fields.Where(f => f.Token == "SERIAL" && f.Source == "barcode"))
            .Select(f => f.SampleValue!)
            .Where(v => v.Length >= 7)
            .ToList();
        if (exact.Count < 2) return;

        var prefixLen = serialStruct?.SamplePrefix.Length ?? 7;
        var prefixes = exact.Select(v => v[..Math.Min(prefixLen, v.Length)]).ToList();
        var consensus = prefixes.GroupBy(p => p).OrderByDescending(g => g.Count()).First();
        if (consensus.Count() < 2) return;
        var prefix = consensus.Key;

        foreach (var f in spec.Labels
                     .SelectMany(l => l.Fields.Where(f => f.Token == "SERIAL" && f.Source == "ocr" && f.SampleValue is { Length: > 7 })))
        {
            var fixed_ = prefix + f.SampleValue![Math.Min(prefix.Length, f.SampleValue.Length)..];
            if (fixed_ != f.SampleValue)
            {
                f.SampleValue = fixed_;
                f.Confidence = 0.6f;
            }
        }
    }

    private static void ResolveKitTable(ContentSpec spec, Dictionary<string, LabelImages> images)
    {
        var kit = spec.Labels.FirstOrDefault(l => l.Fields.Any(f => f.Token == "KIT_TABLE"));
        if (kit is null || kit.KitRows.Count > 0) return;

        // Cac accessory ID label trong cung tai lieu la nguon SACH cho (PartNumber, Serial).
        var accessories = spec.Labels
            .Where(l => l != kit && l.Fields.Any(f => f.Token == "KIT_PN"))
            .Select(l => new KitRow(
                l.Product,
                l.Fields.FirstOrDefault(f => f.Token == "SERIAL")?.SampleValue ?? ""))
            .Where(r => !string.IsNullOrWhiteSpace(r.PartNumber))
            .ToList();

        if (accessories.Count > 0)
        {
            kit.KitRows.AddRange(accessories);
            kit.Notes.Add($"Bang kit dung {accessories.Count} dong tu cac nhan Accessory ID trong tai lieu - xac nhan thu tu.");
            return;
        }

        // Khong co accessory label -> thu doc bang tu OCR cua chinh nhan lit-kit.
        if (!images.TryGetValue(kit.Id, out var img)) return;
        var rowRx = new Regex(@"([0-9A-Z][0-9A-Z\-]{3,})\s+([0-9][0-9A-Z]{7,})", RegexOptions.IgnoreCase);
        foreach (var l in img.OcrLines)
        {
            var m = rowRx.Match(l);
            if (m.Success)
                kit.KitRows.Add(new KitRow(m.Groups[1].Value.ToUpperInvariant(), RepairCode(m.Groups[2].Value, "SERIAL", null)));
        }
        if (kit.KitRows.Count > 0)
            kit.Notes.Add($"Bang kit doc tu OCR ({kit.KitRows.Count} dong) - do tin cay thap, PHAI xac nhan.");
    }

    private static int CountDigits(string s) => s.Count(char.IsDigit);

    // So sanh "long" (O<->0, I/L<->1...) - dung khi doi chieu part number/ma vach.
    private static string NormLoose(string s)
    {
        var up = new string(s.ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        return up.Replace('O', '0').Replace('I', '1').Replace('L', '1').Replace('S', '5')
                 .Replace('B', '8').Replace('E', '0').Replace('C', '0').Replace('Z', '2');
    }

    private static double Ratio(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return 0;
        var d = Levenshtein(a, b);
        return 1.0 - (double)d / Math.Max(a.Length, b.Length);
    }

    private static int Levenshtein(string a, string b)
    {
        var dp = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) dp[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            var prev = dp[0];
            dp[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var tmp = dp[j];
                dp[j] = Math.Min(Math.Min(dp[j] + 1, dp[j - 1] + 1), prev + (a[i - 1] == b[j - 1] ? 0 : 1));
                prev = tmp;
            }
        }
        return dp[b.Length];
    }

    private static int FieldOrder(ContentField a, ContentField b)
    {
        var ia = Array.IndexOf(TokenOrder, a.Token);
        var ib = Array.IndexOf(TokenOrder, b.Token);
        return (ia < 0 ? 99 : ia).CompareTo(ib < 0 ? 99 : ib);
    }

    private static string GuessArchetype(ContentLabel label)
    {
        var tokens = label.Fields.Select(f => f.Token).ToHashSet();
        if (tokens.Contains("KIT_TABLE")) return "lit-kit";
        if (tokens.Contains("DESCRIPTION") || tokens.Contains("UPC") || tokens.Contains("QTY")) return "packing";
        var maxDim = Math.Max(label.WidthMm, label.HeightMm);
        if (tokens.Count > 0 && tokens.All(t => t is "PART_NUMBER" or "KIT_PN" or "SERIAL" or "COUNTRY")
            && maxDim > 0 && maxDim <= 60)
            return "simple-id";
        return "unknown";
    }

    private static string Slug(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "label" : slug;
    }
}
