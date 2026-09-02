using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutoDesignLabel;

/// <summary>Label Definition Model - artifact trung gian giua PMD va may in.</summary>
public sealed class LdmDocument
{
    public string SchemaVersion { get; set; } = "1.0";
    public string DocumentId { get; set; } = "";
    public string Revision { get; set; } = "";
    public string SourcePmd { get; set; } = "";
    public Dictionary<string, FieldDef> Fields { get; set; } = new();
    public List<OpenIssue> OpenIssues { get; set; } = new();
    public List<LabelDef> Labels { get; set; } = new();

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Options dung khi GHI LDM ra file (PmdExtractor / AutoLayoutEngine / Web UI) - phai
    /// co JsonStringEnumConverter de LayoutSource ghi ra "Auto"/"Exact" thay vi so.</summary>
    public static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static LdmDocument Load(string path) =>
        JsonSerializer.Deserialize<LdmDocument>(File.ReadAllText(path), Opts)
        ?? throw new InvalidDataException($"Khong doc duoc LDM: {path}");
}

public sealed class FieldDef
{
    public string Source { get; set; } = "";
    public string? Sample { get; set; }
    public string? Pattern { get; set; }
    public string? Note { get; set; }
}

public sealed class OpenIssue
{
    public string Ref { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Text { get; set; } = "";
}

/// <summary>1 buoc trong "Nhat ky Auto-Design" - giai thich cho nguoi dung (khong phai dev) tool da
/// doc gi tu PMD va quyet dinh ra sao khi dung nhan. Hien tuan tu theo dung thu tu pipeline.</summary>
public sealed class TraceStep
{
    /// <summary>Ma buoc on dinh: input | classify | frame | scope | font | text | tokens | barcode | qr | result.</summary>
    public string Key { get; set; } = "";
    /// <summary>Tieu de ngan, ngon ngu nguoi dung.</summary>
    public string Title { get; set; } = "";
    /// <summary>1 dong "tool da lam gi".</summary>
    public string Did { get; set; } = "";
    /// <summary>Doan giai thich de hieu (vi sao lam vay, y nghia).</summary>
    public string Explain { get; set; } = "";
    /// <summary>auto = khop ro voi PMD; check = dung theo suy luan, nen soat; warn = co diem can xu ly.</summary>
    public string Status { get; set; } = "auto";
    /// <summary>Cac dong van ban trich NGUYEN VAN tu PMD lam bang chung.</summary>
    public List<string> Evidence { get; set; } = new();
    /// <summary>Nguon cua bang chung, vd "PMD - trang 2".</summary>
    public string? EvidenceSource { get; set; }
    /// <summary>Loi nhac "can kiem tra" (neu co).</summary>
    public string? Verify { get; set; }
    /// <summary>Bang phu tuy chon (font/token/element...). Rong = khong hien bang.</summary>
    public List<string> TableColumns { get; set; } = new();
    public List<List<string>> TableRows { get; set; } = new();
}

/// <summary>Nguon toa do cua nhan.
///   Exact = doc truc tiep tu toa do vector trong PMD.pdf (PmdExtractor) hoac LDM viet tay.
///   Auto  = AutoLayoutEngine tu sinh bo cuc tu "content spec" khi PMD khong co toa do vector
///           (vi du PMD tu chuan bi bang PowerPoint roi Save As PDF - mockup la anh raster).
/// Khac han "thieu toa do la loi van kien": voi PMD dang content-only, khong co toa do la BINH
/// THUONG - PMD chi rang buoc kich thuoc + danh sach field, khong quy dinh font/barcode/vi tri.
/// He thong ra soat dung gia tri nay de hien badge "Auto-layout - can review thiet ke" (trung
/// tinh) thay vi bao Blocker/Major/Minor.</summary>
public enum LayoutSource { Exact, Auto }

public sealed class LabelDef
{
    public string Id { get; set; } = "";
    public string PartNumber { get; set; } = "";
    public string Name { get; set; } = "";
    public double WidthMm { get; set; }
    public double HeightMm { get; set; }
    public string Material { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public LayoutSource LayoutSource { get; set; } = LayoutSource.Exact;
    public string? LayoutConfidence { get; set; }

    /// <summary>Ban kinh bo goc cua khung nhan (die-cut), mm. 0 = goc vuong. Suy ra tu duong stroke
    /// bo tron cua mockup trong PMD (xem PmdExtractor). Dung de ve dung hinh nhan trong PDF proof /
    /// Web, khong anh huong toa do phan tu ben trong.</summary>
    public double CornerRadiusMm { get; set; }

    /// <summary>Nhat ky tung buoc PmdExtractor da lam de dung ra nhan nay - de nguoi dung tin va soat
    /// lai logic auto-design (xem tab "Auto-Design" trong Web UI). Rong = LDM viet tay hoac chua co
    /// trace (vd duong content-only). Chi la thong tin, renderer/ZPL/.lab bo qua.
    ///
    /// KHONG serialize vao *.ldm.json: (1) tranh phinh file gap 3 lan, (2) trace chi dung ngay sau
    /// khi trich xuat - LDM da sua tay thi trace thanh loi thoi. Web UI giu doi tuong trong bo nho
    /// (nut "Trace Log" trong popup). CLI ghi ra file &lt;id&gt;.trace.html di kem (xem TraceReport).</summary>
    [JsonIgnore]
    public List<TraceStep> Trace { get; set; } = new();

    /// <summary>False khi PMD khong dinh nghia rieng "Label Font Style & Size" cho nhan nay - dau
    /// hieu day la decal tinh dan san (vd Screw VOID, Safety seal), khong thuoc pham vi auto-design.
    /// Mac dinh true de tuong thich nguoc voi LDM da viet tay truoc day.</summary>
    public bool RequiresAutoDesign { get; set; } = true;

    public List<Element> Elements { get; set; } = new();
}

/// <summary>Toa do tinh bang mm, goc o gop tren-trai cua nhan.</summary>
public sealed class Element
{
    public string Type { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public double X1 { get; set; }
    public double Y1 { get; set; }
    public double X2 { get; set; }
    public double Y2 { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public double Size { get; set; }
    public string Font { get; set; } = "light";

    /// <summary>Ho font. Null = JioType (mac dinh, dung cho nhan Exact theo PMD co spec typographic).
    /// "Helvetica" = font trung tinh cho nhan Auto (PMD content-only khong quy dinh font) - la
    /// standard-14 PDF font nen khong can nhung.</summary>
    public string? FontFamily { get; set; }

    public string Align { get; set; } = "left";
    public string? Text { get; set; }
    public string? Data { get; set; }
    public string Ecc { get; set; } = "M";
    public string? Placeholder { get; set; }
    public double[]? Dash { get; set; }

    // Ho tro khoi lap (6 dong RSN/MAC tren carton label)
    public int Max { get; set; }
    public double Y0 { get; set; }
    public double StepY { get; set; }
    public List<Element>? Template { get; set; }
}

public static class Binder
{
    private static readonly Regex Token = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    public static string Bind(string text, IReadOnlyDictionary<string, string> data) =>
        Token.Replace(text, m => data.TryGetValue(m.Groups[1].Value, out var v)
            ? v
            : throw new KeyNotFoundException($"Thieu du lieu cho truong {{{{{m.Groups[1].Value}}}}}"));

    /// <summary>Danh sach ten token {{...}} xuat hien trong text, khong trung lap - dung cho
    /// LabFileGenerator de tao Free Variable trong CS6 ma khong lam Bind() (giu token song thay vi
    /// thay bang gia tri tinh).</summary>
    public static IEnumerable<string> TokenNames(string text) =>
        Token.Matches(text).Select(m => m.Groups[1].Value).Distinct();

    /// <summary>Bung element 'repeat' thanh danh sach element thuong.</summary>
    public static List<Element> Expand(LabelDef label, IReadOnlyDictionary<string, string> data)
    {
        var result = new List<Element>();
        foreach (var el in label.Elements)
        {
            if (el.Type != "repeat" || el.Template is null) { result.Add(el); continue; }

            var count = data.TryGetValue("UNIT_COUNT", out var c) ? int.Parse(c) : el.Max;
            count = Math.Min(count, el.Max);

            for (var i = 1; i <= count; i++)
            {
                var row = new Dictionary<string, string>(data)
                {
                    ["i"] = i.ToString(),
                    ["RSN_i"] = data[$"RSN{i}"],
                    ["MAC_i"] = data[$"MAC{i}"]
                };
                foreach (var t in el.Template)
                {
                    var e = t.ShallowClone();
                    e.Y = el.Y0 + (i - 1) * el.StepY + t.Y;
                    if (e.Text is not null) e.Text = Bind(e.Text, row);
                    if (e.Data is not null) e.Data = Bind(e.Data, row);
                    result.Add(e);
                }
            }
        }
        return result;
    }

    private static Element ShallowClone(this Element e) => (Element)e.MemberwiseClonePublic();
    private static object MemberwiseClonePublic(this Element e) =>
        JsonSerializer.Deserialize<Element>(JsonSerializer.Serialize(e))!;
}
