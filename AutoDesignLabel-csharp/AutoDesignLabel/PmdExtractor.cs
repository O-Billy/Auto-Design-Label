using System.Text.RegularExpressions;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace AutoDesignLabel;

/// <summary>
/// Doc PMD.pdf va suy ra LdmDocument bang rule-based, khong dung AI, chay hoan toan offline.
///
/// Cach hoat dong: cac mockup trong PMD nay duoc ve bang text/vector THAT (khong phai anh raster) -
/// nen co the doc toa do chinh xac tu chinh PDF thay vi doan bo cuc. Cu the:
///   1. Doc "Quantity/Label Size/Label Material" tu van ban de biet kich thuoc that (mm) cua nhan.
///   2. Tim hinh chu nhat vector (stroke) tren trang co ty le canh khop voi kich thuoc that -> do la
///      khung ngoai cua mockup, tu do suy ra ty le px/mm va goc toa do.
///   3. Moi tu (Word) nam trong khung -> gom thanh dong chu -> Element type=text, dung dung
///      PointSize/baseline PdfPig tra ve (chinh xac tuyet doi, khong uoc luong).
///   4. Cac hinh chu nhat fill (vach mã vạch/o QR) nam trong khung -> gom nhom theo dai Y -> mot
///      dai rieng le la barcode128, nhieu dai xep chong nhau la QR (dung logic quet raster giong nhau).
///   5. Doi chieu voi bang "Font Style & Size" va cac gia tri mau (EAN/MAC/RSN...) trong van ban de
///      gan font medium/light va thay gia tri mau bang {{TOKEN}}.
///
/// Day la cong cu HO TRO (best-effort), khong phai oracle: phan nao khong nhan dien chac chan se
/// duoc ghi vao OpenIssues de nguoi dung tu kiem tra/sua trong buoc xem lai, khong tu y doan bua.
/// Bang alias token (FieldAliasesByLabel) dang tune rieng cho PMD/nha cung cap nay - PMD khac co
/// the can bang alias khac.
/// </summary>
public sealed class PmdExtractor
{
    private const double PtPerMm = 72.0 / 25.4;

    public LdmDocument Extract(string pdfPath) =>
        Extract(File.ReadAllBytes(pdfPath), Path.GetFileName(pdfPath));

    /// <summary>Cho phep goi tu web app (file upload trong bo nho) ma khong can ghi ra dia.</summary>
    public LdmDocument Extract(byte[] pdfBytes, string sourceFileName)
    {
        using var pdf = PdfDocument.Open(pdfBytes);
        var pages = Enumerable.Range(1, pdf.NumberOfPages)
            .Select(n => pdf.GetPage(n))
            .ToList();
        var pageLines = pages.Select(p => BuildLines(p)).ToList(); // nguong rong - tieu de/metadata
        var tightPageLines = pages.Select(p => BuildLines(p, 6, splitOnFontChange: true)).ToList(); // nguong hep - phan tu trong mockup

        var ldm = new LdmDocument { SourcePmd = sourceFileName };
        var allLines = pageLines.SelectMany(l => l).ToList();

        (ldm.DocumentId, ldm.Revision) = ExtractDocInfo(allLines);

        var sections = FindLabelSections(pageLines);
        var tokenCatalog = new Dictionary<string, string>(); // gia tri mau -> {{TOKEN}}

        foreach (var section in sections)
        {
            var label = ExtractLabel(section, pages, pageLines, tightPageLines, tokenCatalog);
            ldm.Labels.Add(label);
        }

        ldm.Fields = tokenCatalog
            .GroupBy(kv => kv.Value)
            .ToDictionary(g => g.Key, g => new FieldDef { Source = "unknown", Sample = g.First().Key });

        ldm.OpenIssues = _warnings
            .Select((w, i) => new OpenIssue { Ref = $"auto-{i + 1}", Severity = "major", Text = w })
            .ToList();

        return ldm;
    }

    private readonly List<string> _warnings = new();
    private void Warn(string labelId, string text) => _warnings.Add($"[{labelId}] {text}");

    // ------------------------------------------------------------------
    // 1) Gom Word thanh dong chu (theo baseline Y, cat khi khoang cach X qua lon)
    // ------------------------------------------------------------------

    private sealed record TextLine(
        int PageNo, double Left, double Right, double Bottom, double Top,
        double BaselineY, double PointSize, string FontWeight, string Text);

    private sealed record TextRun(
        string Text, double Left, double Right, double Top, double Bottom,
        double BaseY, double Size, string Weight, int WordId);

    /// <summary>1 "tu" PdfPig (Word, phan tach theo khoang trang) co the chua nhieu KY TU dinh lien
    /// nhau nhung co CO CHU/DO DAM KHAC NHAU - vd "1N" la so "1" 12pt roi chu "N" 6pt (kieu chi so
    /// tren de bieu thi don vi, "1" don vi "N"). Neu chi lay dinh dang cua ky tu DAU TIEN cho ca tu
    /// (nhu truoc day), phan con lai se bi ve sai kich thuoc/do dam. Ham nay tach 1 Word thanh cac
    /// TextRun con - moi run la 1 day ky tu LIEN TIEP CUNG (do dam, co chu) - de downstream xu ly
    /// dung tung phan, du 2 phan dinh lien nhau khong co khoang trang. wordId danh dau cac run cung
    /// xuat than tu 1 Word - dung o Flush() de biet khi nao CAN chen dau cach khi ghep text lai (chi
    /// giua 2 Word khac nhau, khong giua 2 run cua CUNG 1 Word).</summary>
    private static IEnumerable<TextRun> SplitWordIntoRuns(Word w, int wordId)
    {
        var letters = w.Letters;
        var runStart = 0;
        for (var i = 1; i <= letters.Count; i++)
        {
            var boundary = i == letters.Count
                || FontWeightFromName(letters[i].Font.Name) != FontWeightFromName(letters[runStart].Font.Name)
                || Math.Abs(letters[i].PointSize - letters[runStart].PointSize) > 0.1;
            if (!boundary) continue;

            var run = letters.Skip(runStart).Take(i - runStart).ToList();
            yield return new TextRun(
                string.Concat(run.Select(l => l.Value)),
                run.Min(l => l.BoundingBox.Left),
                run.Max(l => l.BoundingBox.Right),
                run.Max(l => l.BoundingBox.Top),
                run.Min(l => l.BoundingBox.Bottom),
                run[0].StartBaseLine.Y,
                run[0].PointSize,
                FontWeightFromName(run[0].Font.Name),
                wordId);
            runStart = i;
        }
    }

    /// <summary>maxGapPt: nguong khoang cach X toi da giua 2 tu de con coi la cung 1 dong. Dung 2
    /// gia tri khac nhau tuy muc dich: nguong RONG (mac dinh, cho doc tieu de/metadata - vd "label"
    /// va ma part number co the cach nhau ~22pt ma van la cung 1 tieu de) va nguong HEP (khi tach
    /// phan tu trong khung mockup - de KHONG gop nham 2 phan tu thiet ke tach biet co chu dich, vd
    /// "RSN: ..." va "Made in India" cach nhau ~21pt nhung la 2 Element rieng trong PMD).
    ///
    /// splitOnFontChange: cat dong khi TEN FONT nhung trong PDF doi (vd "TEMROR+JioType-Medium" ->
    /// "GAAROR+JioType-Light") HOAC khi co chu (point size) doi giua 2 RUN lien ke cung baseline (vd
    /// "1" 12pt roi "N" 6pt, hoac "1N" 12pt roi "Device" 6pt) - ca 2 la tin hieu CHINH XAC va TONG
    /// QUAT cho moi PMD ve viec "day la 2 phan tu thiet ke khac nhau, khong duoc gop lam 1", tin cay
    /// hon nhieu so voi co IsBold cua PdfPig (font tuy chinh nhung thuong khong bat co nay dung) hay
    /// doi chieu van ban muc "Font Style & Size" (chi ap dung 1 kieu cho ca dong, khong phan biet
    /// duoc phan nhan dam voi phan gia tri nhat, hay phan co lon/nho khac nhau, trong CUNG 1 dong,
    /// tham chi trong CUNG 1 "tu"). Neu khong tach, Flush() se lay co chu/do dam cua RUN DAU TIEN
    /// gan cho CA cum gop - lam phan con lai bi ve sai kich thuoc/kieu chu va de len phan tu ke tiep.
    /// Chi bat khi tach phan tu trong mockup (tight lines) - dong tieu de/metadata (lenient lines)
    /// khong can, vi mot so tieu de co the doi font giua ten nhan va ma part number.</summary>
    private static List<TextLine> BuildLines(Page page, double maxGapPt = 30, bool splitOnFontChange = false)
    {
        var runs = page.GetWords()
            .Where(w => w.Letters.Count > 0)
            .SelectMany((w, idx) => SplitWordIntoRuns(w, idx))
            // Gom theo Y DA LAM TRON truoc (cung "hang doc" du baseline lech vai phan tram diem do
            // font-rendering, van gop dung nhom) roi moi xep trai->phai trong hang - neu sap xep
            // truc tiep theo BaseY chinh xac tuyet doi, 2 tu tren CUNG mot dong nhung baseline lech
            // 0.0x pt co the bi dao thu tu doc (trai/phai sai), lam FindNearbyToken chon nham dong
            // ben phai lam ung cu vien gan nhat thay vi dong ben trai.
            .OrderByDescending(r => Math.Round(r.BaseY))
            .ThenByDescending(r => r.BaseY)
            .ThenBy(r => r.Left)
            .ToList();

        var lines = new List<TextLine>();
        var current = new List<TextRun>();
        double? lastY = null;
        double? lastRight = null;
        string? lastWeight = null;
        double? lastSize = null;

        void Flush()
        {
            if (current.Count == 0) return;
            var text = new System.Text.StringBuilder();
            for (var i = 0; i < current.Count; i++)
            {
                // Chi chen dau cach giua 2 RUN thuoc 2 WORD PdfPig KHAC NHAU (ranh gioi tu that su) -
                // 2 run cua CUNG 1 Word (vd "1" va "N" trong "1N") phai dinh lien nhau, du khoang cach
                // hinh hoc giua chung (do co chu khac nhau) co the LON HON khoang trang thuc su giua
                // 2 tu ke ben - nen KHONG the dung nguong khoang cach X de suy ra co can dau cach hay
                // khong, phai dua vao ranh gioi Word goc tu PdfPig.
                //
                // ...NGOAI TRU khi 2 Word do dinh LIEN nhau (khoang ho X ~0): PdfPig doi khi cat 1
                // chuoi lien tuc (vd "RSN:RTHHGMYSSSSSSSS") thanh 2 Word do subset font - luc do chen
                // dau cach se lam sai gia tri (khong khop token, tran le nhan). Chi chen khi co khoang
                // ho X thuc su (>~0.12em) - dung du nho de van giu dau cach giua 2 tu that.
                if (i > 0 && current[i].WordId != current[i - 1].WordId
                    && current[i].Left - current[i - 1].Right > current[i].Size * 0.12)
                    text.Append(' ');
                text.Append(current[i].Text);
            }
            var left = current.Min(c => c.Left);
            var right = current.Max(c => c.Right);
            var bottom = current.Min(c => c.Bottom);
            var top = current.Max(c => c.Top);
            var baseline = current[0].BaseY;
            var size = current[0].Size;
            var weight = current[0].Weight;
            lines.Add(new TextLine(page.Number, left, right, bottom, top, baseline, size, weight, text.ToString()));
            current.Clear();
        }

        foreach (var r in runs)
        {
            // Dung sai Y that chat (cung dong = cung baseline chinh xac, sai so lam tron) va chi
            // cho phep khoang cach X duong trong nguong maxGapPt - tranh gop nham voi chu thich
            // kich thuoc (vd "5mm") nam gan cung do cao nhung khac dong noi dung, hoac tu nam ben
            // trai tu truoc do. Neu splitOnFontChange, cat them khi ten font HOAC co chu doi - vd
            // "1" 12pt roi "N" 6pt trong cung 1 tu "1N"; neu gop chung thanh 1 TextLine se lay co
            // chu cua RUN DAU TIEN cho ca cum (xem Flush), lam phan con lai bi ve sai kich thuoc va
            // de len phan tu ke tiep.
            var sameLine = lastY.HasValue && Math.Abs(r.BaseY - lastY.Value) <= 0.5
                           && (!lastRight.HasValue || (r.Left - lastRight.Value >= -0.5 && r.Left - lastRight.Value <= maxGapPt))
                           && (!splitOnFontChange || (lastWeight == r.Weight && Math.Abs(lastSize!.Value - r.Size) < 0.1));
            if (!sameLine) Flush();
            current.Add(r);
            lastWeight = r.Weight;
            lastSize = r.Size;
            lastY = r.BaseY;
            lastRight = r.Right;
        }
        Flush();
        return lines;
    }

    // ------------------------------------------------------------------
    // 2) Thong tin chung cua tai lieu
    // ------------------------------------------------------------------

    private static (string, string) ExtractDocInfo(List<TextLine> lines)
    {
        var footer = lines
            .Select(l => Regex.Match(l.Text, @"/([A-Za-z0-9.]+)/(\d+)/\d+\s*of\s*\d+"))
            .FirstOrDefault(m => m.Success);
        var docId = footer?.Groups[1].Value ?? "UNKNOWN";
        var revision = footer?.Groups[2].Value ?? "00";
        return (docId, revision);
    }

    // ------------------------------------------------------------------
    // 3) Tim cac muc nhan ("N. Ten nhan PartNumber") va pham vi trang cua tung muc
    // ------------------------------------------------------------------

    private sealed record LabelSection(string PartNumber, string Name, int StartPage, int EndPage);

    private static readonly Regex HeaderRx =
        new(@"^\d+\.\s+(.+?)\s+(\d{3}\.\d{5}\.\d{3})\s*$", RegexOptions.Compiled);

    private static List<LabelSection> FindLabelSections(List<List<TextLine>> pageLines)
    {
        var headers = new List<(int Page, string Name, string PartNumber)>();
        for (var p = 0; p < pageLines.Count; p++)
            foreach (var line in pageLines[p])
            {
                var m = HeaderRx.Match(line.Text);
                if (m.Success)
                    headers.Add((p + 1, m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            }

        var sections = new List<LabelSection>();
        for (var i = 0; i < headers.Count; i++)
        {
            var endPage = i + 1 < headers.Count ? headers[i + 1].Page - 1 : pageLines.Count;
            if (endPage < headers[i].Page) endPage = headers[i].Page;
            sections.Add(new LabelSection(headers[i].PartNumber, headers[i].Name, headers[i].Page, endPage));
        }
        return sections;
    }

    // ------------------------------------------------------------------
    // 4) Do dam/nhat: doc truc tiep TEN FONT nhung trong PDF theo tung tu (vd "TEMROR+JioType-
    //    Medium" vs "GAAROR+JioType-Light") - tong quat cho moi PMD, khong phu thuoc cach hanh
    //    van muc "Font Style & Size" (vi du khac co the dat ten khac "Bold 6pt", "Semibold"...).
    // ------------------------------------------------------------------

    private static string FontWeightFromName(string fontName) =>
        fontName.Contains("Medium", StringComparison.OrdinalIgnoreCase) ||
        fontName.Contains("Bold", StringComparison.OrdinalIgnoreCase)
            ? "medium" : "light";

    // Dau hieu "nhan nay co dinh nghia typographic rieng, thuoc pham vi auto-design" - xem ghi
    // chu quy tac nghiep vu trong ExtractLabel.
    private static readonly Regex FontStyleHeadingRx =
        new(@"Label\s+Font\s+Style\s*(&|and)\s*Size", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ------------------------------------------------------------------
    // 5) Bang alias token - TUNE RIENG cho PMD/nha cung cap nay (xem doc-comment dau file)
    // ------------------------------------------------------------------

    private static readonly Dictionary<string, Dictionary<string, string>> FieldAliasesByLabel = new()
    {
        ["device label"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MAC ID"] = "MAC", ["EAN"] = "EAN", ["RSN"] = "RSN",
        },
        ["mrp and rsn label"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["MAC ID"] = "MAC", ["RSN"] = "RSN", ["EAN"] = "EAN",
            ["Month & Year of Manufacture"] = "MM_YYYY", ["Gross Weight"] = "GROSS_G",
        },
        ["carton label"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Carton No."] = "CARTON_NO", ["MSN"] = "MSN", ["EAN"] = "EAN",
            ["QNTY"] = "QNTY_CARTON", ["Gross Weight"] = "CARTON_GROSS_G", ["Net Weight"] = "CARTON_NET_G",
        },
        ["pallet label"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PO No."] = "PO_NO", ["Invoice No."] = "INVOICE_NO", ["Pallet No."] = "PALLET_SEQ",
            ["EAN"] = "EAN", ["QNTY"] = "QNTY_PALLET", ["Total No. of Cartons"] = "TOTAL_CARTONS",
            ["Gross Wt."] = "PALLET_GROSS_G", ["Net Wt."] = "PALLET_NET_G",
        },
    };

    private static string SlugId(string name) =>
        Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-').Split('-').FirstOrDefault() ?? "label";

    // ------------------------------------------------------------------
    // 6) Trich xuat 1 nhan: metadata (Quantity/Size/Material) + tim khung mockup + phan tu
    // ------------------------------------------------------------------

    private LabelDef ExtractLabel(
        LabelSection section, List<Page> pages, List<List<TextLine>> pageLines,
        List<List<TextLine>> tightPageLines,
        Dictionary<string, string> tokenCatalog)
    {
        var sectionLines = new List<TextLine>();
        var tightSectionLines = new List<TextLine>();
        for (var p = section.StartPage; p <= section.EndPage; p++)
        {
            sectionLines.AddRange(pageLines[p - 1]);
            tightSectionLines.AddRange(tightPageLines[p - 1]);
        }

        var label = new LabelDef
        {
            Id = SlugId(section.Name),
            PartNumber = section.PartNumber,
            Name = section.Name,
            Quantity = ParseQuantity(sectionLines),
            Material = ParseMaterial(sectionLines),
        };
        var (widthMm, heightMm, panel2WidthMm) = ParseLabelSize(sectionLines);
        label.WidthMm = widthMm;
        label.HeightMm = heightMm;

        // Dung tightSectionLines (khong phai sectionLines) o day - tranh 2 truong Label:Value khac
        // nhau cung hang nhung cach xa (vd "Commodity: ..." va "Pallet No.: 0001/XXXX" tren cung
        // 1 dong) bi gop nham thanh 1 dong lam regex chi bat duoc truong dau tien.
        BuildTokenCatalogFromSection(tightSectionLines, tokenCatalog);

        if (label.WidthMm <= 0 || label.HeightMm <= 0)
        {
            label.LayoutConfidence = "khong doc duoc Label Size - can dien tay";
            Warn(label.Id, "No valid 'Label Size' found in this section.");
            label.Trace = BuildStopTrace(label, section, sectionLines, new TraceStep
            {
                Key = "frame", Title = "Label size could not be read",
                Did = "Looked for a “Label Size: A*B mm” line in this section.",
                Status = "warn",
                Explain = "No valid size line was found in the PMD. Enter Width × Height (mm) manually before this label can be auto-designed.",
                Verify = "Enter the label size from the drawing / paper approval.",
            });
            return label;
        }

        // Quy tac nghiep vu: chi tien hanh auto-design cho nhan ma PMD co dinh nghia rieng "Label
        // Font Style & Size" - day la dau hieu nhan co data-merge that (can chon dung font
        // medium/light cho tung truong) chu khong phai decal tinh dan san (vd Screw VOID, Safety
        // seal khong co spec typographic vi khong in theo lo).
        if (!sectionLines.Any(l => FontStyleHeadingRx.IsMatch(l.Text)))
        {
            label.RequiresAutoDesign = false;
            label.LayoutConfidence =
                "PMD khong dinh nghia 'Label Font Style & Size' cho nhan nay - ngoai pham vi auto-design (decal/nhan tinh, khong co spec typographic).";
            label.Trace = BuildStopTrace(label, section, sectionLines, new TraceStep
            {
                Key = "scope", Title = "Out of auto-design scope",
                Did = "Checked whether the PMD has a “Label Font Style & Size” table for this label.",
                Status = "warn",
                Explain = "The PMD has no font table for this label → it is a pre-printed sticker / decal (e.g. Screw VOID, tamper seal) with no per-batch data merge. The tool skips it and builds no design.",
            });
            return label;
        }

        var mockup = panel2WidthMm > 0
            ? FindDualPanelMockupRect(section, pages, pageLines, label.WidthMm - panel2WidthMm, panel2WidthMm, label.HeightMm)
            : FindMockupRect(section, pages, pageLines, label.WidthMm, label.HeightMm);
        if (mockup is null)
        {
            label.LayoutConfidence = "khong tim thay khung mockup (co the la decal tinh, khong co Label Content dang vector) - can dat layout thu cong";
            Warn(label.Id, "No rectangle matching the aspect ratio found in the PDF - the label may have no variable data (static decal) or the mockup may be a raster image.");
            label.Trace = BuildStopTrace(label, section, sectionLines, new TraceStep
            {
                Key = "frame", Title = "No label frame matching the aspect ratio was found",
                Did = $"Looked for a vector border with a side ratio ≈ {label.WidthMm:0.#}÷{label.HeightMm:0.#} (in either orientation).",
                Status = "warn",
                Explain = "No border matched. The label may be a static decal with no variable data, or the mockup may be embedded as a raster image. The layout must be placed manually.",
                Verify = "Open this label's PMD page and check whether the mockup is drawn with vector strokes.",
            });
            return label;
        }

        // PMD ghi "Label Size: A*B mm" khong nhat quan thu tu - neu khung ve theo huong DAO lai
        // (vd QR Label: text ghi 56*28.5 nhung ve doc 28.5 rong x 56 cao) thi hoan doi cho khop
        // hinh ve THAT, va ghi chu de nguoi dung xac nhan.
        if (mockup.Value.Swapped)
        {
            (label.WidthMm, label.HeightMm) = (label.HeightMm, label.WidthMm);
            Warn(label.Id, $"'Label Size' in the PMD is in a different orientation from the drawing - swapped to {label.WidthMm}x{label.HeightMm}mm to match the mockup. Needs confirmation.");
        }
        if (mockup.Value.CornerRadiusPt > 0)
        {
            var mmPerPt = label.WidthMm / (mockup.Value.Rect.Right - mockup.Value.Rect.Left);
            label.CornerRadiusMm = Math.Round(mockup.Value.CornerRadiusPt * mmPerPt, 2);
        }

        label.LayoutConfidence = "trich xuat tu dong tu toa do vector trong PMD.pdf - can nguoi dung xem lai";
        var geo = new MockupGeometry(mockup.Value.Page, mockup.Value.Rect, label.WidthMm, label.HeightMm);

        // Dung tightPageLines (nguong gop dong hep) o day - trong pham vi mockup can giu cac phan
        // tu thiet ke tach biet rieng ra, khong gop nham thanh 1 dong nhu khi doc van ban thuong.
        var linesInBox = tightPageLines[mockup.Value.PageNo - 1]
            .Where(l => geo.Contains(l.Left, l.Bottom, l.Right, l.Top))
            .OrderBy(l => l.PageNo).ThenByDescending(l => l.BaselineY)
            .ToList();
        if (Environment.GetEnvironmentVariable("PMD_DEBUG") == "1")
        {
            Console.Error.WriteLine($"[DEBUG {label.Id}] linesInBox.Count={linesInBox.Count} rect=[{mockup.Value.Rect.Left:F1},{mockup.Value.Rect.Bottom:F1}->{mockup.Value.Rect.Right:F1},{mockup.Value.Rect.Top:F1}]");
            foreach (var l in pageLines[mockup.Value.PageNo - 1])
                Console.Error.WriteLine($"  page-line: [{l.Left:F1},{l.Bottom:F1}->{l.Right:F1},{l.Top:F1}] \"{l.Text}\"");
        }

        // Ten muc lay tu header co the con dau ':' o cuoi (vd "Device Label:") - chuan hoa (bo ky tu
        // khong phai chu/so/khoang trang) truoc khi tra bang alias, neu khong bang alias se KHONG BAO
        // GIO khop va barcode canh caption "RSN:"/"MAC ID:" se khong gan duoc token.
        var nameKey = Regex.Replace(section.Name.ToLowerInvariant(), @"[^a-z0-9 ]", "").Trim();
        var aliases = FieldAliasesByLabel.TryGetValue(nameKey, out var a)
            ? a : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in linesInBox)
        {
            var (xMm, yMm) = geo.ToMm(line.Left, line.BaselineY);
            var text = SubstituteTokens(line.Text, tokenCatalog, aliases, label.Id);
            label.Elements.Add(new Element
            {
                Type = "text",
                X = Math.Round(xMm, 2),
                Y = Math.Round(yMm, 2),
                Size = Math.Round(line.PointSize, 2),
                Font = line.FontWeight,
                Align = "left",
                Text = text,
            });
        }

        // Noi dung XML cua QR thuong nam o muc "Barcode/QR Definition" - co the o trang khac voi
        // trang chua mockup - nen tim tren toan bo pham vi trang cua muc nay, khong chi 1 trang.
        ExtractShapes(mockup.Value.Page, geo, linesInBox, sectionLines, label, aliases, tokenCatalog);
        ExtractRasterCodes(mockup.Value.Page, geo, linesInBox, sectionLines, label, aliases, tokenCatalog);
        ExtractLines(mockup.Value.Page, geo, label);

        BuildLabelTrace(label, section, sectionLines, aliases, tokenCatalog, (
            FrameWpt: mockup.Value.Rect.Right - mockup.Value.Rect.Left,
            FrameHpt: mockup.Value.Rect.Top - mockup.Value.Rect.Bottom,
            Swapped: mockup.Value.Swapped));
        return label;
    }

    // ------------------------------------------------------------------
    // 6b) Nhat ky Auto-Design - giai thich cho nguoi dung (khong phai dev) cach nhan duoc dung.
    //     Toan bo dung lai tu ket qua da co (label.Elements, _warnings, sectionLines) - khong lam
    //     thay doi logic trich xuat.
    // ------------------------------------------------------------------

    private static string TraceTrunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static string TraceTypeVi(string t) => t switch
    {
        "text" => "Text", "barcode128" => "Code128 barcode", "qr" => "QR code",
        "line" => "Line", "image" => "Image", "repeat" => "Repeat block", _ => t
    };

    private List<string> MyWarnings(string labelId) =>
        _warnings.Where(w => w.StartsWith($"[{labelId}] ", StringComparison.Ordinal))
                 .Select(w => w[(labelId.Length + 3)..].Trim())
                 .ToList();

    private static TraceStep StepInput(LabelDef label, LabelSection section, List<TextLine> sectionLines)
    {
        var header = sectionLines.FirstOrDefault(l => HeaderRx.IsMatch(l.Text))?.Text.Trim();
        var qty = sectionLines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"Quantity\s*:", RegexOptions.IgnoreCase))?.Text.Trim();
        var sizeLn = sectionLines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"Label Size\s*:", RegexOptions.IgnoreCase))?.Text.Trim();
        return new TraceStep
        {
            Key = "input", Title = "Identify which label this is in the PMD",
            Did = "Found a section heading like “N. Label Name  part-number”, then grouped the pages under it.",
            Status = "auto", EvidenceSource = $"PMD · page {section.StartPage}",
            Evidence = new[] { header, qty, sizeLn }.Where(x => x is not null).Select(x => x!).ToList(),
            Explain = $"The tool treats this section as “{label.Name.TrimEnd(':', ' ')}”, part number {label.PartNumber}"
                    + (label.Quantity > 0 ? $", print quantity {label.Quantity}" : "") + ".",
        };
    }

    private static TraceStep StepClassify() => new()
    {
        Key = "classify", Title = "The mockup is vector — coordinates read exactly",
        Did = "Checked the mockup: text and strokes are real vector art (not a raster image).",
        Status = "auto",
        Explain = "Because the mockup is vector, the tool reads the position and size of every detail straight from the PDF — no layout guessing. "
                + "If the mockup were just an image (exported from PowerPoint), the tool would take the other path: only pull the field list, then build a default layout itself.",
    };

    private List<TraceStep> BuildStopTrace(LabelDef label, LabelSection section, List<TextLine> sectionLines, TraceStep stop)
    {
        var t = new List<TraceStep> { StepInput(label, section, sectionLines), StepClassify() };
        if (label.WidthMm > 0 && label.HeightMm > 0 && stop.Key != "frame")
            t.Add(new TraceStep
            {
                Key = "frame", Title = "Label size",
                Did = "Read the “Label Size” line in the PMD.",
                Status = "auto",
                Explain = $"Read {label.WidthMm:0.#} × {label.HeightMm:0.#} mm.",
            });
        t.Add(stop);
        return t;
    }

    private void BuildLabelTrace(
        LabelDef label, LabelSection section, List<TextLine> sectionLines,
        Dictionary<string, string> aliases, Dictionary<string, string> tokenCatalog,
        (double FrameWpt, double FrameHpt, bool Swapped) fr)
    {
        var pg = section.StartPage;
        var warns = MyWarnings(label.Id);
        var t = new List<TraceStep> { StepInput(label, section, sectionLines), StepClassify() };

        // 3 - frame
        var sizeLn = sectionLines.FirstOrDefault(l => Regex.IsMatch(l.Text, @"Label Size\s*:", RegexOptions.IgnoreCase))?.Text.Trim()
                     ?? $"Label Size: {label.WidthMm:0.#}*{label.HeightMm:0.#}mm";
        var frameRatio = fr.FrameHpt > 0 ? fr.FrameWpt / fr.FrameHpt : 0;
        var fst = new TraceStep
        {
            Key = "frame", Title = "Find the die-cut frame & lock the mm ↔ pt scale",
            Did = "Read “Label Size”, then found the border whose side ratio matches, to use as the coordinate origin.",
            Status = fr.Swapped ? "check" : "auto", EvidenceSource = $"PMD · page {pg}",
        };
        fst.Evidence.Add($"{sizeLn}  →  {label.WidthMm:0.#} × {label.HeightMm:0.#} mm");
        fst.Evidence.Add($"border found: {fr.FrameWpt:0.#} × {fr.FrameHpt:0.#} pt  ·  ratio {frameRatio:0.00}");
        fst.Evidence.Add(fr.Swapped
            ? "drawn orientation is REVERSED vs the order in “Label Size” → swapped width ↔ height to match the drawing"
            : $"ratio {frameRatio:0.00} ≈ {label.WidthMm:0.#}÷{label.HeightMm:0.#}  ·  orientation correct");
        if (label.CornerRadiusMm > 0)
            fst.Evidence.Add($"border is a ROUNDED rectangle (4 sides + 4 arcs) → radius ≈ {label.CornerRadiusMm:0.#} mm");
        fst.Explain = "The border (rounded or not) is the real die-cut line of the label. The tool uses it as the base frame: "
                    + "the top-left corner of the frame = coordinate (0, 0), and 1 mm on the label = 2.835 pt in the file. "
                    + "The corner radius is recorded so the label shape prints correctly on the proof — it does not affect the position of anything inside.";
        if (fr.Swapped)
            fst.Verify = "The “Label Size” line in the PMD gives the two numbers not in width×height order. The tool swapped them to match the drawing — re-check the label orientation against the paper approval.";
        t.Add(fst);

        // 4 - scope
        t.Add(new TraceStep
        {
            Key = "scope", Title = "Label has variable data — should be auto-designed",
            Did = "Checked whether the PMD has a “Label Font Style & Size” table for this label.",
            Status = "auto", EvidenceSource = $"PMD · page {pg}",
            Evidence = { (sectionLines.FirstOrDefault(l => FontStyleHeadingRx.IsMatch(l.Text))?.Text.Trim() ?? "Label Font Style & Size") + "  →  present" },
            Explain = "A dedicated font table means the label is printed per batch, merging real data (serial, MAC…). "
                    + "Otherwise — like a “Screw VOID” sticker or a tamper seal — no such table means the tool skips it, because it is a pre-printed decal.",
        });

        // 5 - font
        var med = label.Elements.Count(e => e.Type == "text" && e.Font == "medium");
        var lgt = label.Elements.Count(e => e.Type == "text" && e.Font == "light");
        var fontLines = sectionLines
            .Where(l => l.Text.Contains("JioType", StringComparison.OrdinalIgnoreCase)
                     || Regex.IsMatch(l.Text, @"\b\d+\s*pt\b")
                     || Regex.IsMatch(l.Text, @"^\s*[a-e]\.\s"))
            .Select(l => l.Text.Trim()).Distinct().Take(4).ToList();
        t.Add(new TraceStep
        {
            Key = "font", Title = "Assign bold / light per individual glyph",
            Did = "Not inferred from prose — read the embedded font name in the PDF for each character.",
            Status = "auto", EvidenceSource = $"PMD · page {pg}, Font Style table",
            Evidence = fontLines,
            Explain = "Text carrying the font name “…JioType-Medium” → Medium (bold); “…JioType-Light” → Light. "
                    + $"This tracks the drawing exactly and stays correct for any PMD using a different font. This label: {med} bold elements, {lgt} light elements.",
        });

        // 6 - text
        var texts = label.Elements.Where(e => e.Type == "text").ToList();
        var textStep = new TraceStep
        {
            Key = "text", Title = $"{texts.Count} text lines → {texts.Count} elements, with mm coordinates",
            Did = "Each text line inside the frame becomes one element: position (mm, from the top-left corner) and font size taken straight from the PDF.",
            Status = "auto",
            TableColumns = { "Content", "x, y (mm)", "Size", "Font" },
            Explain = "Text lines centred on each other (e.g. “INDIA / CERTIFICATE NO. …”) are marked as one centred block so they do not drift when printed.",
        };
        foreach (var e in texts.OrderBy(e => e.Y).ThenBy(e => e.X).Take(6))
            textStep.TableRows.Add(new List<string>
            {
                TraceTrunc(e.Text ?? "", 40), $"{e.X:0.##}, {e.Y:0.##}",
                $"{e.Size:0.#} pt", e.Font == "medium" ? "Medium" : "Light",
            });
        t.Add(textStep);

        // 7 - tokens
        var usedTokens = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var e in label.Elements)
            foreach (Match m in Regex.Matches((e.Text ?? "") + " " + (e.Data ?? ""), @"\{\{(\w+)\}\}"))
                usedTokens.Add(m.Groups[1].Value);
        var tokStep = new TraceStep
        {
            Key = "tokens", Title = "Replace sample values with {{TOKEN}} so CodeSoft merges real data",
            Did = "Found the sample value after each caption and replaced it with a variable — the variable stays “live” in the .lab file.",
            Status = "auto",
        };
        if (usedTokens.Count == 0)
        {
            tokStep.Explain = "This label has no variable data — all text is fixed (“Fix print”).";
        }
        else
        {
            tokStep.TableColumns = new List<string> { "Caption in PMD", "Sample value", "Variable" };
            foreach (var tok in usedTokens)
            {
                var sample = tokenCatalog.FirstOrDefault(kv => kv.Value == tok).Key;
                var caption = aliases.FirstOrDefault(kv => kv.Value == tok).Key;
                tokStep.TableRows.Add(new List<string> { caption ?? "—", sample ?? "—", "{{" + tok + "}}" });
            }
            tokStep.Explain = "Strings the PMD explicitly marks “Fix print” are kept as-is, not turned into variables.";
        }
        t.Add(tokStep);

        // 8 - barcode
        var bcs = label.Elements.Where(e => e.Type == "barcode128").ToList();
        if (bcs.Count > 0)
        {
            var bcStep = new TraceStep
            {
                Key = "barcode",
                Title = bcs.Count == 1 ? "1 Code128 barcode" : $"Bar strip → split into {bcs.Count} separate Code128 barcodes",
                Did = "Measured the gaps between bars: a gap clearly wider than the narrowest bar = the boundary between two separate barcodes.",
                Status = "auto",
                TableColumns = { "Barcode at x (mm)", "Content" },
                Explain = "Each barcode's content is inferred from the text label aligned below / beside it (e.g. a barcode left-aligned with “RSN:” → {{RSN}}). "
                        + "If a barcode is a raster image in the PMD, the tool takes the HRI string as a placeholder and flags it for review.",
            };
            foreach (var b in bcs.OrderBy(e => e.Y).ThenBy(e => e.X))
                bcStep.TableRows.Add(new List<string> { $"{b.X:0.#}  (width {b.Width:0.#} mm)", b.Data ?? "—" });
            var bcWarn = warns.Where(w => w.StartsWith("Barcode", StringComparison.OrdinalIgnoreCase)).ToList();
            if (bcWarn.Count > 0) { bcStep.Status = "check"; bcStep.Verify = string.Join("  ", bcWarn); }
            t.Add(bcStep);
        }

        // 9 - qr
        var qr = label.Elements.FirstOrDefault(e => e.Type == "qr");
        if (qr is not null)
        {
            var found = (qr.Data ?? "").TrimStart().StartsWith("<");
            var qrStep = new TraceStep
            {
                Key = "qr", Title = "Recognise the module cluster → QR, take content from PMD",
                Did = "A cluster of many small cells, near-square bounding box, high ink density → a QR code.",
                Status = found ? "check" : "warn",
                EvidenceSource = found ? "PMD · QR Definition section — variables substituted" : null,
            };
            if (found)
                qrStep.Evidence = (qr.Data ?? "").Replace("\r", "").Split('\n')
                    .Where(x => x.TrimStart().StartsWith("<") && !x.TrimStart().StartsWith("<?") && !x.TrimStart().StartsWith("<!"))
                    .Take(7).Select(x => x.Trim()).ToList();
            qrStep.Explain = found
                ? $"QR is ≈ {qr.Size:0.#} mm wide. The QR holds many fields, so CodeSoft cannot merge variables into it — the tool bakes the content as static text at the current values. "
                  + "When RSN / EAN / MAC change, the design file MUST be regenerated for the QR to be correct."
                : $"QR is ≈ {qr.Size:0.#} mm wide but no content XML block was found in the PMD — it must be assigned manually.";
            if (found)
                qrStep.Verify = "Re-check that the fields inside the QR match the text shown on the label.";
            t.Add(qrStep);
        }

        // 10 - result
        var byType = label.Elements.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).ToList();
        var resStep = new TraceStep
        {
            Key = "result",
            Title = $"{label.Elements.Count} elements · {(warns.Count == 0 ? "no warnings" : $"{warns.Count} points to review")}",
            Did = "Wrote the .ldm.json file. The print linter (barcode X-dimension, QR quiet zone, margin overflow, collisions) runs at the 1:1 preview step — see the “Printer Specs” tab.",
            Status = warns.Count > 0 ? "warn" : "auto",
            TableColumns = { "Type", "Count" },
        };
        foreach (var g in byType)
            resStep.TableRows.Add(new List<string> { TraceTypeVi(g.Key), g.Count().ToString() });
        var notes = new List<string>(warns)
        {
            "Vector graphics (logos, RoHS / WEEE symbols…) in the PMD are not extracted automatically — add them manually if the print needs them.",
        };
        resStep.Verify = string.Join("   •   ", notes);
        t.Add(resStep);

        label.Trace = t;
    }

    private static int ParseQuantity(List<TextLine> lines)
    {
        foreach (var l in lines)
        {
            var m = Regex.Match(l.Text, @"Quantity\s*:\s*(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        return 1;
    }

    private static string ParseMaterial(List<TextLine> lines)
    {
        foreach (var l in lines)
        {
            var m = Regex.Match(l.Text, @"Label Material\s*:\s*(.+)$", RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value.Trim();
        }
        return "";
    }

    /// <summary>Panel2WidthMm > 0 nghia la nhan duoc khai bao dang "W1*Hmm+W2*Hmm" (vi du mockup
    /// chinh + tag QR/mã vạch rời cat theo duong "print on line") - PMD ve day thanh 2 khung rieng
    /// trong PDF, khong phai 1 khung tong, nen buoc do khung phai xu ly rieng (xem FindMockupRect).</summary>
    private static (double WidthMm, double HeightMm, double Panel2WidthMm) ParseLabelSize(List<TextLine> lines)
    {
        foreach (var l in lines)
        {
            var m = Regex.Match(l.Text,
                @"Label Size\s*:\s*(\d+(?:\.\d+)?)\s*\*\s*(\d+(?:\.\d+)?)\s*mm(?:\s*\+\s*(\d+(?:\.\d+)?)\s*\*\s*(\d+(?:\.\d+)?)\s*mm)?",
                RegexOptions.IgnoreCase);
            if (m.Success)
            {
                var w1 = double.Parse(m.Groups[1].Value);
                var h1 = double.Parse(m.Groups[2].Value);
                var w2 = m.Groups[3].Success ? double.Parse(m.Groups[3].Value) : 0;
                return (w1 + w2, h1, w2);
            }
            var d = Regex.Match(l.Text, @"Label Size\s*:\s*D\s*(\d+(?:\.\d+)?)\s*mm", RegexOptions.IgnoreCase);
            if (d.Success)
            {
                var dia = double.Parse(d.Groups[1].Value);
                return (dia, dia, 0);
            }
        }
        return (0, 0, 0);
    }

    // ------------------------------------------------------------------
    // 7) Tim khung mockup: hinh chu nhat stroke tren cac trang cua muc, ty le canh khop widthMm/heightMm
    //
    // Khung nay co the la:
    //   - hinh chu nhat goc vuong (4 doan thang), HOAC
    //   - hinh chu nhat BO GOC / die-cut (4 doan thang + 4 cung bezier). PdfPig tra ve
    //     GetBoundingRectangle() = hinh bao NGOAI dung bang WxH that nen viec khop ty le van chinh
    //     xac; ta chi can them: (a) nhan biet duong bo goc de VE lai dung hinh, (b) uoc luong ban
    //     kinh bo goc.
    //
    // Text "Label Size: A*B mm" trong PMD KHONG nhat quan thu tu (co PMD ghi Rong*Cao, co cho ghi
    // canh-lon truoc bat ke huong). Nen ta khop ty le theo CA HAI chieu (A/B va B/A); neu khung ve
    // theo chieu DAO thi bao caller hoan doi Width/Height cho khop hinh ve that.
    // ------------------------------------------------------------------

    private static (int PageNo, Page Page, PdfRectangle Rect, bool Swapped, double CornerRadiusPt)? FindMockupRect(
        LabelSection section, List<Page> pages, List<List<TextLine>> pageLines, double widthMm, double heightMm)
    {
        var targetRatio = widthMm / heightMm;
        const double ratioTol = 0.06;
        // Diem: uu tien khung KHOP DUNG HUONG, chua NHIEU CHU, la duong BO GOC (die-cut that su),
        // dien tich lon. Khung "kich thuoc minh hoa" (rong, khong chu ben trong) se thua diem.
        (int PageNo, Page Page, PdfRectangle Rect, bool Swapped, double RadiusPt, double Score, double Area)? best = null;

        for (var p = section.StartPage; p <= section.EndPage; p++)
        {
            var page = pages[p - 1];
            foreach (var path in page.Paths)
            {
                if (!path.IsStroked) continue;
                var bb = path.GetBoundingRectangle();
                if (!bb.HasValue) continue;
                var w = bb.Value.Right - bb.Value.Left;
                var h = bb.Value.Top - bb.Value.Bottom;
                if (w < 20 || h < 20) continue; // qua nho, khong phai khung nhan
                if (w > page.Width * 0.95 || h > page.Height * 0.95) continue; // gan het trang, khong phai
                var ratio = w / h;
                var errDirect = Math.Abs(ratio - targetRatio) / targetRatio;
                var errSwapped = Math.Abs(ratio - 1.0 / targetRatio) / (1.0 / targetRatio);
                if (errDirect > ratioTol && errSwapped > ratioTol) continue;
                var swapped = errDirect > ratioTol; // chi coi la "dao huong" khi KHONG khop dung huong

                var area = w * h;
                var wordCount = pageLines[p - 1].Count(l =>
                    l.Left >= bb.Value.Left - 1.5 && l.Right <= bb.Value.Right + 1.5 &&
                    l.Bottom >= bb.Value.Bottom - 1.5 && l.Top <= bb.Value.Top + 1.5);
                var rounded = IsRoundedRectPath(path);

                var score = wordCount * 10
                            + (swapped ? 0 : 3)     // khop dung huong -> nhinh hon
                            + (rounded ? 2 : 0);     // duong die-cut that -> nhinh hon khung minh hoa
                if (best is null || score > best.Value.Score ||
                    (Math.Abs(score - best.Value.Score) < 0.001 && area > best.Value.Area))
                    best = (p, page, bb.Value, swapped,
                            rounded ? EstimateCornerRadiusPt(path, w, h) : 0, score, area);
            }
        }
        return best is null ? null
            : (best.Value.PageNo, best.Value.Page, best.Value.Rect, best.Value.Swapped, best.Value.RadiusPt);
    }

    /// <summary>Duong hinh chu nhat BO GOC: 1 subpath, 4 doan thang (4 canh) + 4 cung CubicBezier
    /// (4 goc). Cho phep xe dich de chiu duoc bien the giua cac trinh xuat PDF.</summary>
    private static bool IsRoundedRectPath(UglyToad.PdfPig.Graphics.PdfPath path)
    {
        var subs = path.ToList();
        if (subs.Count != 1) return false;
        var cmds = subs[0].Commands;
        var lines = cmds.Count(c => c is PdfSubpath.Line);
        var curves = cmds.Count(c => c is PdfSubpath.CubicBezierCurve || c is PdfSubpath.QuadraticBezierCurve);
        return curves is >= 3 and <= 5 && lines is >= 3 and <= 8;
    }

    /// <summary>Uoc luong ban kinh bo goc (pt). Voi hinh chu nhat bo goc chuan, tong do dai 4 canh
    /// THANG = 2*(w-2r) + 2*(h-2r) = 2w + 2h - 8r  =>  r = (2w + 2h - tongCanhThang) / 8.
    /// Cong thuc nay du chinh xac de VE lai va khong phu thuoc cach PdfPig phan ra tung cung goc.</summary>
    private static double EstimateCornerRadiusPt(UglyToad.PdfPig.Graphics.PdfPath path, double w, double h)
    {
        var straight = path.First().Commands.OfType<PdfSubpath.Line>().Sum(l => l.Length);
        var r = (2 * w + 2 * h - straight) / 8.0;
        return r > 0.3 && r < Math.Min(w, h) / 2 ? r : 0;
    }

    /// <summary>Cho nhan dang "panel chinh + tag rieng cat theo duong print-on-line" (vi du MRP+RSN
    /// label): PMD ve day thanh 2 khung stroke RIENG BIET nam canh nhau, khong phai 1 khung tong.
    /// Tim 1 cap khung: mot khung khop ty le panel1/heightMm, mot khung khop ty le panel2/heightMm,
    /// cung dai Y va nam sat nhau theo X - roi tra ve BOUNDING BOX GOP CUA CA HAI, de tai su dung
    /// nguyen MockupGeometry/Contains/ToMm nhu truong hop 1 khung don.</summary>
    private static (int PageNo, Page Page, PdfRectangle Rect, bool Swapped, double CornerRadiusPt)? FindDualPanelMockupRect(
        LabelSection section, List<Page> pages, List<List<TextLine>> pageLines,
        double panel1WidthMm, double panel2WidthMm, double heightMm)
    {
        var ratio1 = panel1WidthMm / heightMm;
        var ratio2 = panel2WidthMm / heightMm;
        (int PageNo, Page Page, PdfRectangle Rect, int WordCount, double Area)? best = null;

        for (var p = section.StartPage; p <= section.EndPage; p++)
        {
            var page = pages[p - 1];
            var candidates = page.Paths
                .Where(path => path.IsStroked)
                .Select(path => path.GetBoundingRectangle())
                .Where(bb => bb.HasValue)
                .Select(bb => bb!.Value)
                .Where(bb => bb.Right - bb.Left >= 20 && bb.Top - bb.Bottom >= 20)
                .Where(bb => bb.Right - bb.Left <= page.Width * 0.95 && bb.Top - bb.Bottom <= page.Height * 0.95)
                .ToList();

            for (var i = 0; i < candidates.Count; i++)
            {
                var r1 = candidates[i];
                var w1 = r1.Right - r1.Left;
                var h1 = r1.Top - r1.Bottom;
                var ratioA = w1 / h1;
                var matchesPanel1 = Math.Abs(ratioA - ratio1) / ratio1 <= 0.06;
                var matchesPanel2 = Math.Abs(ratioA - ratio2) / ratio2 <= 0.06;
                if (!matchesPanel1 && !matchesPanel2) continue;
                var targetOtherRatio = matchesPanel1 ? ratio2 : ratio1;

                for (var j = 0; j < candidates.Count; j++)
                {
                    if (i == j) continue;
                    var r2 = candidates[j];
                    var w2 = r2.Right - r2.Left;
                    var h2 = r2.Top - r2.Bottom;
                    var ratioB = w2 / h2;
                    if (Math.Abs(ratioB - targetOtherRatio) / targetOtherRatio > 0.06) continue;

                    // Cung dai Y (do cao gan bang nhau) va nam sat nhau theo X (co the co khe ho
                    // nho o giua cho duong cat/duong chia panel).
                    var sameHeightBand = Math.Abs(r1.Top - r2.Top) <= 3 && Math.Abs(r1.Bottom - r2.Bottom) <= 3;
                    var adjacentX = (r2.Left - r1.Right >= -2 && r2.Left - r1.Right <= 20) ||
                                     (r1.Left - r2.Right >= -2 && r1.Left - r2.Right <= 20);
                    if (!sameHeightBand || !adjacentX) continue;

                    var combined = new PdfRectangle(
                        Math.Min(r1.Left, r2.Left), Math.Min(r1.Bottom, r2.Bottom),
                        Math.Max(r1.Right, r2.Right), Math.Max(r1.Top, r2.Top));
                    var area = (combined.Right - combined.Left) * (combined.Top - combined.Bottom);
                    var wordCount = pageLines[p - 1].Count(l =>
                        l.Left >= combined.Left - 1.5 && l.Right <= combined.Right + 1.5 &&
                        l.Bottom >= combined.Bottom - 1.5 && l.Top <= combined.Top + 1.5);

                    if (best is null || wordCount > best.Value.WordCount ||
                        (wordCount == best.Value.WordCount && area > best.Value.Area))
                        best = (p, page, combined, wordCount, area);
                }
            }
        }
        return best is null ? null : (best.Value.PageNo, best.Value.Page, best.Value.Rect, false, 0.0);
    }

    private readonly struct MockupGeometry
    {
        private readonly PdfRectangle _rect;
        private readonly double _scale; // mm per pt
        public MockupGeometry(Page page, PdfRectangle rect, double widthMm, double heightMm)
        {
            _rect = rect;
            _scale = widthMm / (double)(rect.Right - rect.Left);
        }
        public bool Contains(double left, double bottom, double right, double top)
        {
            const double tol = 1.5;
            return left >= _rect.Left - tol && right <= _rect.Right + tol &&
                   bottom >= _rect.Bottom - tol && top <= _rect.Top + tol;
        }
        public (double xMm, double yMm) ToMm(double px, double py) =>
            ((px - (double)_rect.Left) * _scale, ((double)_rect.Top - py) * _scale);
        public double LenMm(double lenPt) => lenPt * _scale;
    }

    // ------------------------------------------------------------------
    // 7b) Duong ke thang (gach chan duoi text, duong phan cach...) - PMD ve bang net STROKE rieng
    // biet voi van ban (PdfPig KHONG tu gan underline vao Letter/Word), nen phai tu tim va tai tao
    // lai. Tong quat cho moi PMD: BAT KY net stroke nao gan nhu THANG (be RONG hoac be CAO gan bang
    // 0 - tuc 1 doan thang ngang/doc, khong phai hinh 2 chieu) deu la 1 duong ke that trong thiet
    // ke, tru khung ngoai cua mockup (da xu ly rieng o FindMockupRect/FindDualPanelMockupRect va
    // KHONG lot qua bo loc nay vi ca 2 chieu cua no deu lon, vd >=20pt).
    // ------------------------------------------------------------------

    private const double LineFlatTolerancePt = 0.5;
    private const double MinLineLengthPt = 2.0;

    private static void ExtractLines(Page page, MockupGeometry geo, LabelDef label)
    {
        foreach (var path in page.Paths)
        {
            if (!path.IsStroked) continue;
            var bb = path.GetBoundingRectangle();
            if (!bb.HasValue) continue;
            var r = bb.Value;
            if (!geo.Contains((double)r.Left, (double)r.Bottom, (double)r.Right, (double)r.Top)) continue;

            var w = (double)r.Right - (double)r.Left;
            var h = (double)r.Top - (double)r.Bottom;
            var isHorizontal = h <= LineFlatTolerancePt && w > MinLineLengthPt;
            var isVertical = w <= LineFlatTolerancePt && h > MinLineLengthPt;
            if (!isHorizontal && !isVertical) continue; // hinh 2 chieu (vd khung mockup) - khong phai duong ke

            var (x1Mm, y1Mm) = geo.ToMm((double)r.Left, (double)r.Bottom);
            var (x2Mm, y2Mm) = geo.ToMm((double)r.Right, (double)r.Top);
            label.Elements.Add(new Element
            {
                Type = "line",
                X1 = Math.Round(x1Mm, 2), Y1 = Math.Round(y1Mm, 2),
                X2 = Math.Round(x2Mm, 2), Y2 = Math.Round(y2Mm, 2),
            });
        }
    }

    // ------------------------------------------------------------------
    // 8) Phan cum cac hinh fill trong khung thanh barcode/QR (xem doc-comment dau file)
    // ------------------------------------------------------------------

    private void ExtractShapes(
        Page page, MockupGeometry geo, List<TextLine> linesInBox, List<TextLine> sectionLines,
        LabelDef label, Dictionary<string, string> aliases, Dictionary<string, string> tokenCatalog)
    {
        var fills = page.Paths
            .Where(p => p.IsFilled)
            .Select(p => p.GetBoundingRectangle())
            .Where(bb => bb.HasValue)
            .Select(bb => bb!.Value)
            .Where(bb => geo.Contains(bb.Left, bb.Bottom, bb.Right, bb.Top))
            .ToList();

        // 8a) QR ve bang nhieu O MODULE ROI RAC (khong xep thanh dai Y deu) - buoc gom-theo-Y ben duoi
        // bo sot kieu nay. Gom cum lien thong roi nhan cum "gan vuong + nhieu o + mat do muc cao".
        // QR ve kieu "dai ngang lien tuc" (vd B01O023) khong tao cum kieu nay va van di tiep xuong
        // logic gom-theo-Y -> ra QR nhu cu.
        var qrRects = FindQrClusters(fills, geo);
        foreach (var qr in qrRects)
        {
            var (qxMm, qyMm) = geo.ToMm((double)qr.Left, (double)qr.Top);
            var qSizeMm = geo.LenMm(Math.Max((double)qr.Right - (double)qr.Left, (double)qr.Top - (double)qr.Bottom));
            var qContent = FindQrContent(sectionLines, tokenCatalog);
            label.Elements.Add(new Element
            {
                Type = "qr", X = Math.Round(qxMm, 2), Y = Math.Round(qyMm, 2),
                Size = Math.Round(qSizeMm, 2), Ecc = "M",
                Data = qContent ?? "QR CONTENT UNDEFINED",
            });
            if (qContent is null)
                Warn(label.Id, $"QR at ({qxMm:F1},{qyMm:F1})mm - XML content could not be determined, assign manually.");
        }
        if (qrRects.Count > 0)
            fills = fills.Where(f => !qrRects.Any(q =>
                (double)f.Left >= (double)q.Left - 0.5 && (double)f.Right <= (double)q.Right + 0.5 &&
                (double)f.Bottom >= (double)q.Bottom - 0.5 && (double)f.Top <= (double)q.Top + 0.5)).ToList();

        // Gom theo dai Y (lam tron 0.3pt) - moi dai la 1 "hang" fill lien tuc (1 vach hoac 1 hang module QR).
        // Giu lai ca danh sach Fills tho (khong chi Left/Right gop) - can de tach 2 barcode dat CANH
        // NHAU tren CUNG 1 hang (vd cot RSN va cot MAC tren carton label) - xem SplitBarcodeRow.
        var rowGroups = fills
            .GroupBy(bb => (Bottom: Math.Round((double)bb.Bottom, 1), Top: Math.Round((double)bb.Top, 1)))
            .Where(g => g.Count() >= 15)
            .Select(g => new
            {
                g.Key.Bottom, g.Key.Top,
                Left = g.Min(b => (double)b.Left),
                Right = g.Max(b => (double)b.Right),
                Fills = g.ToList(),
            })
            .OrderByDescending(g => g.Top)
            .ToList();

        // Gop cac dai lien nhau (khoang cach Y nho, X chong lan nhieu) thanh 1 "shape"
        var used = new bool[rowGroups.Count];
        var shapes = new List<(double Left, double Right, double Bottom, double Top, int RowCount, List<PdfRectangle>? SingleRowFills)>();
        for (var i = 0; i < rowGroups.Count; i++)
        {
            if (used[i]) continue;
            var cluster = new List<int> { i };
            used[i] = true;
            var changed = true;
            while (changed)
            {
                changed = false;
                for (var j = 0; j < rowGroups.Count; j++)
                {
                    if (used[j]) continue;
                    foreach (var ci in cluster.ToList())
                    {
                        var a = rowGroups[ci]; var b = rowGroups[j];
                        var yGap = Math.Max(a.Bottom, b.Bottom) - Math.Min(a.Top, b.Top);
                        var xOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
                        var xSpan = Math.Min(a.Right - a.Left, b.Right - b.Left);
                        if (yGap <= 2.0 && xSpan > 0 && xOverlap / xSpan > 0.5)
                        {
                            cluster.Add(j); used[j] = true; changed = true;
                            break;
                        }
                    }
                }
            }
            var items = cluster.Select(k => rowGroups[k]).ToList();
            shapes.Add((items.Min(x => x.Left), items.Max(x => x.Right),
                        items.Min(x => x.Bottom), items.Max(x => x.Top), items.Count,
                        items.Count == 1 ? items[0].Fills : null));
        }

        foreach (var shape in shapes)
        {
            if (shape.RowCount == 1)
            {
                // Barcode 1D chi la 1 hang duy nhat (khong xep chong nhieu hang nhu QR). Nhung neu
                // 2 barcode KHAC NHAU (vd cot RSN va cot MAC) dat cung do cao/cung Y tren cung 1
                // hang trong PMD, buoc gom-theo-Y o tren se gop nham thanh 1 rowGroup duy nhat. Tach
                // lai truoc khi tao Element - xem SplitBarcodeRow.
                foreach (var (segLeft, segRight) in SplitBarcodeRow(shape.SingleRowFills!))
                {
                    var (sxMm, syMm) = geo.ToMm(segLeft, shape.Top);
                    var swMm = geo.LenMm(segRight - segLeft);
                    var shMm = geo.LenMm(shape.Top - shape.Bottom);

                    // Luu y: KHONG dung cu phap {{TOKEN}} cho placeholder chua xac dinh - Binder.Bind
                    // se nem KeyNotFoundException vi khong co du lieu that cho no. Dung van ban
                    // thuong de pipeline render/lint van chay duoc, hien thi ngay tren PDF de nguoi
                    // dung thay cho can sua.
                    var dataToken = FindNearbyToken(segLeft, segRight, shape.Bottom, linesInBox, aliases, tokenCatalog);
                    label.Elements.Add(new Element
                    {
                        Type = "barcode128", X = Math.Round(sxMm, 2), Y = Math.Round(syMm, 2),
                        Width = Math.Round(swMm, 2), Height = Math.Round(shMm, 2),
                        Data = dataToken ?? "UNDEFINED",
                    });
                    if (dataToken is null)
                        Warn(label.Id, $"Barcode at ({sxMm:F1},{syMm:F1})mm - content could not be determined, assign manually.");
                }
            }
            else
            {
                var (xMm, yMm) = geo.ToMm(shape.Left, shape.Top);
                var wMm = geo.LenMm(shape.Right - shape.Left);
                var hMm = geo.LenMm(shape.Top - shape.Bottom);
                var qrData = FindQrContent(sectionLines, tokenCatalog);
                label.Elements.Add(new Element
                {
                    Type = "qr", X = Math.Round(xMm, 2), Y = Math.Round(yMm, 2),
                    Size = Math.Round(Math.Max(wMm, hMm), 2), Ecc = "M",
                    Data = qrData ?? "QR CONTENT UNDEFINED",
                });
                if (qrData is null)
                    Warn(label.Id, $"QR at ({xMm:F1},{yMm:F1})mm - XML content could not be determined, assign manually.");
            }
        }
    }

    // ------------------------------------------------------------------
    // 8c) Ma vach / QR nhung duoi dang ANH RASTER trong mockup (khong phai vector) - vd pallet label
    // cua B01G017. Khong doc duoc ma tran module tu anh, nhung van tao duoc 1 phan tu barcode128
    // (anh det ngang = ma 1D) hoac qr (anh gan vuong) DUNG VI TRI/KICH THUOC de proof + .lab + ZPL
    // co cho dat. Noi dung suy tu nhan text lan can; khong duoc thi de "CHUA-XAC-DINH" + canh bao.
    // ------------------------------------------------------------------

    private void ExtractRasterCodes(
        Page page, MockupGeometry geo, List<TextLine> linesInBox, List<TextLine> sectionLines,
        LabelDef label, Dictionary<string, string> aliases, Dictionary<string, string> tokenCatalog)
    {
        foreach (var img in page.GetImages())
        {
            var b = img.Bounds;
            if (!geo.Contains(b.Left, b.Bottom, b.Right, b.Top)) continue;

            var wMm = geo.LenMm(b.Width);
            var hMm = geo.LenMm(b.Height);
            if (wMm < 5 || hMm < 2) continue; // icon/vun trang tri, khong phai ma

            // Tranh tao trung: neu da co barcode/qr vector chong len vi tri anh nay (vd anh chi la
            // lop nen) thi bo qua.
            var (xMm, yMm) = geo.ToMm(b.Left, b.Top);
            var overlapsExisting = label.Elements.Any(e =>
                (e.Type == "barcode128" || e.Type == "qr") &&
                xMm < e.X + Math.Max(e.Width, e.Size) + 2 && xMm + wMm > e.X - 2 &&
                yMm < e.Y + Math.Max(e.Height, e.Size) + 2 && yMm + hMm > e.Y - 2);
            if (overlapsExisting) continue;

            var aspect = b.Width / Math.Max((double)b.Height, 0.1);
            if (aspect >= 2.5)
            {
                var token = FindNearbyToken(b.Left, b.Right, b.Bottom, linesInBox, aliases, tokenCatalog);
                // Khong map duoc token: mã vạch 1D gan nhu luon in HRI (chuoi ky tu) ngay DUOI no -
                // dung chinh chuoi do lam noi dung tam de proof ra dung, van canh bao de nguoi dung
                // xac nhan / thay bang bien that.
                var hri = token is not null ? null : linesInBox
                    .Where(l => l.Top <= b.Bottom + 4 && l.Top >= b.Bottom - 22)
                    .Where(l => Math.Min(b.Right, l.Right) - Math.Max(b.Left, l.Left) > 0)
                    .Where(l => BareValueRx.IsMatch(l.Text.Trim()))
                    .OrderBy(l => b.Bottom - l.Top)
                    .FirstOrDefault()?.Text.Trim();
                label.Elements.Add(new Element
                {
                    Type = "barcode128", X = Math.Round(xMm, 2), Y = Math.Round(yMm, 2),
                    Width = Math.Round(wMm, 2), Height = Math.Round(hMm, 2),
                    Data = token ?? hri ?? "UNDEFINED",
                });
                Warn(label.Id, (token ?? hri) is null
                    ? $"Barcode (raster image) at ({xMm:F1},{yMm:F1})mm - content could not be read, assign manually."
                    : $"Barcode (raster image) at ({xMm:F1},{yMm:F1})mm - took content '{token ?? hri}' from the HRI as a placeholder, needs confirmation.");
            }
            else if (aspect is >= 0.6 and <= 1.7 && Math.Min(wMm, hMm) >= 6)
            {
                var qrData = FindQrContent(sectionLines, tokenCatalog);
                label.Elements.Add(new Element
                {
                    Type = "qr", X = Math.Round(xMm, 2), Y = Math.Round(yMm, 2),
                    Size = Math.Round(Math.Max(wMm, hMm), 2), Ecc = "M",
                    Data = qrData ?? "QR CONTENT UNDEFINED",
                });
                Warn(label.Id, qrData is null
                    ? $"Raster IMAGE QR at ({xMm:F1},{yMm:F1})mm - content could not be read, assign manually."
                    : $"Raster IMAGE QR at ({xMm:F1},{yMm:F1})mm - XML content was inferred, needs confirmation.");
            }
        }
    }

    /// <summary>Nhan dien QR ve bang nhieu o module roi rac: gom cac o fill thanh cum lien thong
    /// (khoang ho toi da ~vai lan kich thuoc o), roi giu lai cum trong nhu QR - gan VUONG, DU NHIEU
    /// o, moi o NHO so voi cum, va MAT DO muc (dien tich o / dien tich bao) du cao. Loai tru: mã vạch
    /// 1D (bbox rat det), logo/hinh minh hoa (it o hoac co 1 o rat to, mat do thap). Chay TRUOC buoc
    /// gom-theo-Y va lay cac o da nhan la QR ra khoi pool - phan con lai moi di tiep tim barcode.</summary>
    private static List<PdfRectangle> FindQrClusters(List<PdfRectangle> fills, MockupGeometry geo)
    {
        var result = new List<PdfRectangle>();
        var n = fills.Count;
        if (n < 60) return result;

        var cellSizes = fills
            .Select(f => Math.Min((double)f.Right - (double)f.Left, (double)f.Top - (double)f.Bottom))
            .Where(s => s > 0).OrderBy(s => s).ToList();
        if (cellSizes.Count == 0) return result;
        var medianCell = cellSizes[cellSizes.Count / 2];
        var tol = Math.Max(medianCell * 4, 2.5); // o module ke nhau co the cach nhau vai module trang

        var used = new bool[n];
        for (var i = 0; i < n; i++)
        {
            if (used[i]) continue;
            var stack = new Stack<int>();
            stack.Push(i); used[i] = true;
            var members = new List<int>();
            while (stack.Count > 0)
            {
                var k = stack.Pop();
                members.Add(k);
                var a = fills[k];
                for (var j = 0; j < n; j++)
                {
                    if (used[j]) continue;
                    var b = fills[j];
                    var near = (double)b.Left <= (double)a.Right + tol && (double)b.Right >= (double)a.Left - tol
                            && (double)b.Bottom <= (double)a.Top + tol && (double)b.Top >= (double)a.Bottom - tol;
                    if (near) { used[j] = true; stack.Push(j); }
                }
            }
            if (members.Count < 60) continue;

            var left = members.Min(m => (double)fills[m].Left);
            var right = members.Max(m => (double)fills[m].Right);
            var bottom = members.Min(m => (double)fills[m].Bottom);
            var top = members.Max(m => (double)fills[m].Top);
            var w = right - left;
            var h = top - bottom;
            if (w <= 0 || h <= 0) continue;

            var ratio = w / h;
            if (ratio < 0.8 || ratio > 1.25) continue;                // QR gan vuong
            // QR ve toi uu co the gom nhieu module cung mau thanh 1 hinh chu nhat LON (o day co
            // mieng ~0.5*canh) - nen KHONG loc theo be rong 1 o, ma loc theo DIEN TICH: neu 1 hinh
            // chiem qua nua dien tich bao -> do la nen/khung dac (logo/hinh minh hoa), khong phai QR.
            var bboxArea = w * h;
            var biggestCellArea = members.Max(m =>
                ((double)fills[m].Right - (double)fills[m].Left) * ((double)fills[m].Top - (double)fills[m].Bottom));
            if (biggestCellArea > bboxArea * 0.5) continue;
            var ink = members.Sum(m => ((double)fills[m].Right - (double)fills[m].Left) * ((double)fills[m].Top - (double)fills[m].Bottom));
            if (ink / bboxArea < 0.35) continue;                       // QR ~40-55% den; hinh net thi thua hon
            if (geo.LenMm(Math.Max(w, h)) < 4) continue;               // qua nho de la QR that

            result.Add(new PdfRectangle(left, bottom, right, top));
        }
        return result;
    }

    /// <summary>1 "hang" fill (cung do cao Y) co the thuc ra la 2 barcode128 VAT LY KHAC NHAU dat
    /// canh nhau (vd cot RSN va cot MAC tren cung 1 dong cua carton label) - buoc gom-theo-Y trong
    /// ExtractShapes khong phan biet duoc vi chi nhin do cao, khong nhin khoang trong giua chung.
    /// Ham nay tach lai dua tren khoang cach giua 2 vach LIEN KE: cac khoang trong BEN TRONG 1
    /// barcode that (giua cac module/vach cua Code128) khong bao gio vuot qua vai lan be rong vach
    /// RONG NHAT trong chinh no (Code128 gioi han toi da ~4 module lien tiep cho 1 vach/khoang), nen
    /// 1 khoang trong LON HON HAN muc do do (dung boi so nhan an toan) chi co the la khoang trang
    /// THAT giua 2 barcode rieng biet, khong phai 1 phan cua encoding. Dung nguong TUONG DOI (theo
    /// be rong MODULE HEP nhat cua chinh hang do) thay vi 1 so pt co dinh - de tu thich ung voi moi
    /// X-dimension/DPI/PMD khac nhau, khong hardcode theo 1 kich thuoc barcode cu the.
    ///
    /// Lay module hep theo PHAN VI ~12% (khong lay Min tuyet doi) de bo qua vai vach sliver do PDF
    /// lam tron toa do - Min co the la 1 mieng 0.0x pt lam nguong tut xuong qua thap. Nhan 10: 1
    /// khoang trang Code128 toi da 4 module hep, ×10 la bien an toan rong nhung van du nho de tach
    /// 2 ma vach dat sat nhau (khe giua chung thuong >= 20-30 module hep).</summary>
    private static List<(double Left, double Right)> SplitBarcodeRow(List<PdfRectangle> fills)
    {
        var sorted = fills.OrderBy(b => (double)b.Left).ToList();
        var barWidths = sorted.Select(b => (double)b.Right - (double)b.Left).OrderBy(w => w).ToList();
        var narrowModule = barWidths[Math.Min(barWidths.Count - 1, barWidths.Count / 8)];
        var splitGapThreshold = narrowModule * 10;

        var segments = new List<(double Left, double Right)>();
        var segStart = (double)sorted[0].Left;
        var segEnd = (double)sorted[0].Right;
        for (var i = 1; i < sorted.Count; i++)
        {
            var gap = (double)sorted[i].Left - segEnd;
            if (gap > splitGapThreshold)
            {
                segments.Add((segStart, segEnd));
                segStart = (double)sorted[i].Left;
            }
            segEnd = Math.Max(segEnd, (double)sorted[i].Right);
        }
        segments.Add((segStart, segEnd));
        return segments;
    }

    private static string? FindNearbyToken(
        double left, double right, double bottom, List<TextLine> linesInBox,
        Dictionary<string, string> aliases, Dictionary<string, string> tokenCatalog)
    {
        // Nhan/HRI di kem barcode nam NGAY DUOI barcode (canh duoi = bottom), khong phai dong noi
        // dung phia TREN. Chi xet dong co canh tren (Top) tu ~4pt tren canh duoi barcode tro XUONG
        // (toi ~22pt) - neu mo cua so len phia tren (nhu truoc) thi dong "Power Rating/EAN" ngay
        // tren barcode se bi chon nham. Trong so dong ung vien: uu tien do gan Y (lam tron truoc de
        // 2 dong thuc ra cung cao do khong bi dao thu tu), roi den do khop canh trai voi barcode.
        var candidate = linesInBox
            .Where(l => l.Top <= bottom + 4 && l.Top >= bottom - 22)
            .Where(l => Math.Min(right, l.Right) - Math.Max(left, l.Left) > -40) // gan ve X
            .OrderBy(l => Math.Round(bottom - l.Top))
            .ThenBy(l => Math.Abs(left - l.Left))
            .FirstOrDefault();
        if (candidate is null) return null;

        // 1 dong caption co the chua NHIEU cap "Nhan: gia tri" (vd nguong gop hep van dinh
        // "RSN: ... MAC ID: ..." thanh 1 dong) - khi do 2 barcode dat canh nhau tren dong tren se
        // deu "StartsWith" cai dau tien. Chon alias co vi tri (uoc luong theo chi so ky tu tren be
        // rong dong) GAN CANH TRAI cua doan barcode nhat.
        var multiHits = aliases
            .Select(kv => (kv.Value, Idx: candidate.Text.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase)))
            .Where(t => t.Idx >= 0)
            .ToList();
        if (multiHits.Count > 1 && candidate.Text.Length > 0)
        {
            var lineWidth = candidate.Right - candidate.Left;
            return "{{" + multiHits
                .OrderBy(t => Math.Abs(candidate.Left + lineWidth * t.Idx / candidate.Text.Length - left))
                .First().Value + "}}";
        }

        foreach (var kv in aliases)
            if (candidate.Text.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
            {
                // Truong hop dac biet Pallet No. (xem BuildTokenCatalogFromSection) - 2 y nghia
                // khac nhau tuy hinh dang gia tri lien ke, khong the chi dua vao ten nhan.
                if (kv.Key.Equals("Pallet No.", StringComparison.OrdinalIgnoreCase))
                {
                    var nearVal = linesInBox
                        .Where(l2 => BareValueRx.IsMatch(l2.Text))
                        .Where(l2 => Math.Abs(l2.BaselineY - candidate.BaselineY) <= 3.0 && l2.Left >= candidate.Right - 1)
                        .OrderBy(l2 => l2.Left)
                        .FirstOrDefault();
                    if (nearVal is not null && nearVal.Text.StartsWith("RP", StringComparison.OrdinalIgnoreCase) && !nearVal.Text.Contains('/'))
                        return "{{PALLET_NO}}";
                }
                return "{{" + kv.Value + "}}";
            }

        foreach (var kv in tokenCatalog)
            if (candidate.Text.Contains(kv.Key)) return "{{" + kv.Value + "}}";

        return null;
    }

    /// <summary>Tim khoi noi dung QR: bat dau tu dong "&lt;?xml...", gom cac dong lien tiep bat dau
    /// bang '&lt;' (dang the XML) cho den khi gap dong khong con dang the nua.</summary>
    private static string? FindQrContent(List<TextLine> sectionLines, Dictionary<string, string> tokenCatalog)
    {
        var startIdx = sectionLines.FindIndex(l => l.Text.TrimStart().StartsWith("<?xml"));
        if (startIdx < 0) return null;

        var block = new List<string>();
        for (var i = startIdx; i < sectionLines.Count; i++)
        {
            var t = sectionLines[i].Text.TrimStart();
            if (!t.StartsWith("<")) break;
            block.Add(sectionLines[i].Text);
        }
        var content = string.Join("\n", block);
        foreach (var kv in tokenCatalog.OrderByDescending(k => k.Key.Length))
            content = SafeReplace(content, kv.Key, "{{" + kv.Value + "}}");
        return content;
    }

    /// <summary>Thay chuoi con nhu Replace binh thuong, TRU truong hop gia tri can thay la 1 ky tu
    /// lap lai (vd placeholder "XXXXXX") - khi do chi thay neu ky tu ngay truoc/sau vi tri khop
    /// KHONG PHAI cung ky tu do, de tranh khop nham vao GIUA mot chuoi lap dai hon cua field khac
    /// (vd "XXXXXX" cua Gross Weight khop nham vao trong "RPXXXXXXXXPPPP" cua Pallet No.).</summary>
    private static string SafeReplace(string text, string oldValue, string newValue)
    {
        if (oldValue.Length == 0 || oldValue.Distinct().Count() != 1)
            return text.Replace(oldValue, newValue);

        var ch = oldValue[0];
        var pattern = $"(?<!{Regex.Escape(ch.ToString())}){Regex.Escape(oldValue)}(?!{Regex.Escape(ch.ToString())})";
        return Regex.Replace(text, pattern, newValue.Replace("$", "$$"));
    }

    // ------------------------------------------------------------------
    // 9) Bat gia tri mau (vd "EAN: 8699037702050") de xay tu dien gia tri -> {{TOKEN}}
    // ------------------------------------------------------------------

    // Cho phep '/' trong gia tri - cac field dang "so-thu-tu/tong-so" nhu Carton No. "CCCC/9999"
    // hay Pallet No. "0001/XXXX" rat pho bien trong PMD nay.
    private static readonly Regex LabelValueRx =
        new(@"^([A-Za-z][A-Za-z0-9 .&]{1,30}?)\s*[:：]\s*([A-Za-z0-9/]{6,40})\b", RegexOptions.Compiled);
    private static readonly Regex BareLabelRx =
        new(@"^([A-Za-z][A-Za-z0-9 .&]{1,30}?)\s*[:：]\s*$", RegexOptions.Compiled);
    private static readonly Regex BareValueRx =
        new(@"^[A-Za-z0-9/]{6,40}$", RegexOptions.Compiled);

    private void BuildTokenCatalogFromSection(List<TextLine> lines, Dictionary<string, string> catalog)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            // Truong hop 1: "Label: Value ..." tren cung 1 dong (khong dung $ o cuoi vi dong co the
            // con text theo sau, vd bi gop chung voi nhan lan can nhu "RSN: XXX Made in India").
            var m = LabelValueRx.Match(lines[i].Text);
            if (m.Success)
            {
                TryRegisterToken(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim(), catalog);
                continue;
            }

            // Truong hop 2: "Label:" va gia tri la MOT element rieng, gan ve toa do (cung dong hoac
            // ngay ben phai) - vd "MSN:" va "MXXXXXXXXDDMYSCCCC" la 2 Element khac nhau trong mockup.
            // Tim theo khoang cach hinh hoc thay vi vi tri lien ke trong danh sach (thu tu danh sach
            // co the bi xen boi cot khac tren cung 1 trang).
            var bareLabel = BareLabelRx.Match(lines[i].Text);
            if (bareLabel.Success)
            {
                var nearestValue = lines
                    .Where(l2 => BareValueRx.IsMatch(l2.Text))
                    .Where(l2 => Math.Abs(l2.BaselineY - lines[i].BaselineY) <= 3.0 && l2.Left >= lines[i].Right - 1)
                    .OrderBy(l2 => l2.Left)
                    .FirstOrDefault();
                if (nearestValue is null) continue;

                var labelText = bareLabel.Groups[1].Value.Trim();
                var valueText = nearestValue.Text.Trim();

                // Truong hop dac biet: nhan Pallet co 2 "Pallet No.:" mang y nghia khac nhau - so
                // thu tu trong lo hang (dang "0001/XXXX", da bat o Truong hop 1 vi nam cung dong)
                // va ma pallet DUY NHAT dang barcode rieng (bat dau "RP", KHONG co dau "/") - phai
                // phan biet bang chinh hinh dang gia tri, khong the chi dua vao ten nhan.
                if (labelText.Equals("Pallet No.", StringComparison.OrdinalIgnoreCase) &&
                    valueText.StartsWith("RP", StringComparison.OrdinalIgnoreCase) && !valueText.Contains('/'))
                {
                    if (!catalog.ContainsKey(valueText)) catalog[valueText] = "PALLET_NO";
                    continue;
                }

                TryRegisterToken(labelText, valueText, catalog);
            }
        }
    }

    private static void TryRegisterToken(string label, string value, Dictionary<string, string> catalog)
    {
        // Khong loc theo "phai co chu so" - mau placeholder cua RSN la "RTHMDMYXXXXXXXX" (toan chu,
        // khong co so nao). Da loc du chinh xac boi buoc so khop ten field trong alias ben duoi.
        if (catalog.ContainsKey(value)) return;
        foreach (var aliasMap in FieldAliasesByLabel.Values)
            foreach (var kv in aliasMap)
                if (label.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))
                    catalog[value] = kv.Value;
    }

    private static string SubstituteTokens(
        string text, Dictionary<string, string> tokenCatalog, Dictionary<string, string> aliases, string labelId)
    {
        foreach (var kv in tokenCatalog.OrderByDescending(k => k.Key.Length))
            text = SafeReplace(text, kv.Key, "{{" + kv.Value + "}}");
        return text;
    }
}
