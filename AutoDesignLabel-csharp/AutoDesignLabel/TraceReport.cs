using System.Globalization;
using System.Text;

namespace AutoDesignLabel;

/// <summary>
/// Xuat "Nhat ky Auto-Design" (LabelDef.Trace) ra HTML tu-chua (offline, mo bang trinh duyet /
/// in ra giay) de nguoi dung tai ve / luu ho so review. Trace khong nam trong *.ldm.json (xem
/// LabelDef.Trace) nen day la cach mang no ra ngoai.
/// Dung chung: Web UI (nut "Tai Trace Log") va CLI (Program.cs ghi &lt;id&gt;.trace.html).
/// </summary>
public static class TraceReport
{
    private static readonly Dictionary<string, string> EyebrowVi = new()
    {
        ["input"] = "INPUT",
        ["classify"] = "PMD CLASSIFICATION",
        ["frame"] = "SIZE &amp; FRAME",
        ["scope"] = "AUTO-DESIGN SCOPE",
        ["font"] = "FONT RULES",
        ["text"] = "TEXT ELEMENTS",
        ["tokens"] = "DATA VARIABLES",
        ["barcode"] = "BARCODE",
        ["qr"] = "QR CODE",
        ["result"] = "RESULT &amp; CHECKS",
    };

    private static string StatusVi(string s) => s switch
    {
        "warn" => "Warning",
        "check" => "Needs confirmation",
        _ => "Automatic",
    };

    // ------------------------------------------------------------------
    // HTML report
    // ------------------------------------------------------------------

    public static string ToHtml(LdmDocument doc)
    {
        var sb = new StringBuilder();
        // Chi ghep "— <ma tai lieu>" khi that su doc duoc ma tu footer PMD; "UNKNOWN"/rong = an di.
        var idSuffix = string.IsNullOrWhiteSpace(doc.DocumentId) || doc.DocumentId == "UNKNOWN"
            ? "" : $" — {H(doc.DocumentId)}";
        sb.Append("<!doctype html><html lang=\"vi\"><head><meta charset=\"utf-8\">");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        sb.Append($"<title>Auto-Design Trace Log{idSuffix}</title>");
        sb.Append("<style>").Append(Css).Append("</style></head><body><div class=\"wrap\">");

        sb.Append("<header class=\"doc-head\">");
        sb.Append("<p class=\"kicker\">AUTO-DESIGN · EXTRACTION EXPLAINED</p>");
        sb.Append($"<h1>Auto-Design Trace Log{idSuffix}</h1>");
        sb.Append($"<p class=\"meta\">Source PMD: <b>{H(doc.SourcePmd)}</b> · Generated: {DateTime.Now:yyyy-MM-dd HH:mm}</p>");
        sb.Append("<p class=\"intro\">Every auto-designed label comes with the step-by-step log below: what the tool "
                + "<b>read from the PMD file</b>, <b>how it decided</b>, and <b>what you should double-check</b> "
                + "before publishing. No step uses AI or guesswork — every coordinate comes straight from "
                + "the vector strokes in the PDF.</p>");
        sb.Append("</header>");

        foreach (var label in doc.Labels.Where(l => l.Trace.Count > 0))
        {
            var conf = label.Trace.Any(s => s.Status == "warn") ? ("Low", "low")
                     : label.Trace.Any(s => s.Status == "check") ? ("Medium", "mid") : ("High", "high");
            var toCheck = label.Trace.Count(s => s.Status != "auto");

            sb.Append("<section class=\"label\">");
            sb.Append("<div class=\"label-head\">");
            sb.Append("<div class=\"preview\">").Append(LabelSvg(label)).Append("</div>");
            sb.Append("<div class=\"label-info\">");
            sb.Append($"<h2>{H(label.Name.TrimEnd(':', ' '))} <span>· {H(label.PartNumber)}</span></h2>");
            sb.Append($"<p class=\"dims\">{label.WidthMm.ToString("0.#", CultureInfo.InvariantCulture)} × "
                    + $"{label.HeightMm.ToString("0.#", CultureInfo.InvariantCulture)} mm"
                    + (label.CornerRadiusMm > 0 ? $" · rounded corners ~{label.CornerRadiusMm.ToString("0.#", CultureInfo.InvariantCulture)} mm" : "")
                    + "</p>");
            sb.Append("<div class=\"stats\">");
            sb.Append($"<span class=\"stat conf-{conf.Item2}\"><b>{conf.Item1}</b>Confidence</span>");
            sb.Append($"<span class=\"stat\"><b>{label.Elements.Count}</b>elements</span>");
            sb.Append($"<span class=\"stat\"><b>{toCheck}</b>steps to review</span>");
            sb.Append("</div></div></div>");

            sb.Append("<ol class=\"log\">");
            var n = 0;
            foreach (var s in label.Trace)
            {
                n++;
                var eye = EyebrowVi.GetValueOrDefault(s.Key, H(s.Key.ToUpperInvariant()));
                sb.Append($"<li class=\"step s-{s.Status}\"><span class=\"marker\">{n}</span><div class=\"card\">");
                sb.Append($"<div class=\"card-head\"><span class=\"eyebrow\">{eye}</span>"
                        + $"<span class=\"pill {s.Status}\">{StatusVi(s.Status)}</span></div>");
                sb.Append($"<h3>{H(s.Title)}</h3>");
                if (!string.IsNullOrWhiteSpace(s.Did))
                    sb.Append($"<p class=\"did\">{H(s.Did)}</p>");
                if (s.Evidence.Count > 0)
                {
                    sb.Append("<div class=\"evidence\">");
                    if (s.EvidenceSource is not null)
                        sb.Append($"<span class=\"src\">{H(s.EvidenceSource)}</span>");
                    foreach (var ev in s.Evidence) sb.Append($"<div>{H(ev)}</div>");
                    sb.Append("</div>");
                }
                if (s.TableColumns.Count > 0 && s.TableRows.Count > 0)
                {
                    sb.Append("<div class=\"tablewrap\"><table><thead><tr>");
                    foreach (var c in s.TableColumns) sb.Append($"<th>{H(c)}</th>");
                    sb.Append("</tr></thead><tbody>");
                    foreach (var row in s.TableRows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row) sb.Append($"<td>{H(cell)}</td>");
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table></div>");
                }
                if (!string.IsNullOrWhiteSpace(s.Explain))
                    sb.Append($"<p class=\"explain\">{H(s.Explain)}</p>");
                if (s.Verify is not null)
                    sb.Append($"<div class=\"verify s-{s.Status}\"><span>{H(s.Verify)}</span></div>");
                sb.Append("</div></li>");
            }
            sb.Append("</ol></section>");
        }

        var skipped = doc.Labels.Where(l => l.Trace.Count == 0).ToList();
        if (skipped.Count > 0)
        {
            sb.Append("<section class=\"label skipped\"><h2>Labels with no trace log</h2><ul>");
            foreach (var l in skipped)
                sb.Append($"<li><b>{H(l.Name.TrimEnd(':', ' '))}</b> ({H(l.PartNumber)}) — "
                        + (l.RequiresAutoDesign
                            ? "built by auto-layout or a hand-authored LDM."
                            : "out of auto-design scope (decal / pre-printed sticker).") + "</li>");
            sb.Append("</ul></section>");
        }

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // So do nhan 1:1 (SVG) - dung chung cho HTML report va Web UI popup.
    // Ma vach / QR ve bang mau gia (seed on dinh) - du de nhan hinh dang, khong phai ma that.
    // ------------------------------------------------------------------

    public static string LabelSvg(LabelDef l)
    {
        if (l.WidthMm <= 0 || l.HeightMm <= 0) return "";
        var k = Math.Min(600.0 / l.WidthMm, 224.0 / l.HeightMm);
        double pw = l.WidthMm * k, ph = l.HeightMm * k;
        const double mmPerPt = 25.4 / 72.0;
        var b = new StringBuilder();
        b.Append($"<svg viewBox=\"0 0 {N(pw)} {N(ph)}\" width=\"{N(pw)}\" height=\"{N(ph)}\" class=\"adl-lbl\" "
               + "preserveAspectRatio=\"xMidYMid meet\" style=\"overflow:hidden\" "
               + "xmlns=\"http://www.w3.org/2000/svg\" role=\"img\" aria-label=\"Label preview\">");
        var r = l.CornerRadiusMm > 0 ? l.CornerRadiusMm * k : 2;
        b.Append($"<rect x=\"0.75\" y=\"0.75\" width=\"{N(pw - 1.5)}\" height=\"{N(ph - 1.5)}\" "
               + $"rx=\"{N(r)}\" ry=\"{N(r)}\" fill=\"#ffffff\" stroke=\"#9aa3ac\" stroke-width=\"1\"/>");

        foreach (var e in l.Elements)
        {
            switch (e.Type)
            {
                case "text":
                {
                    var s = e.Text ?? "";
                    var isTok = s.Contains("{{");
                    var fs = Math.Max(e.Size * mmPerPt * k, 4);
                    b.Append($"<text x=\"{N(e.X * k)}\" y=\"{N(e.Y * k)}\" font-size=\"{N(fs)}\" font-family=\"sans-serif\" ");
                    if (e.Font == "medium") b.Append("font-weight=\"700\" ");
                    b.Append($"fill=\"{(isTok ? "#8A3606" : "#1b2027")}\">{H(s)}</text>");
                    break;
                }
                case "barcode128":
                {
                    double bx = e.X * k, bw = e.Width * k, by = e.Y * k, bh = Math.Max(e.Height * k, 6);
                    var rnd = new Random(Seed(e.Data ?? "bc") ^ (int)(e.X * 13));
                    for (double x = bx; x < bx + bw;)
                    {
                        var w = 0.35 * k * (1 + rnd.Next(3));
                        if (rnd.NextDouble() > 0.42)
                            b.Append($"<rect x=\"{N(x)}\" y=\"{N(by)}\" width=\"{N(Math.Max(w * 0.6, 0.7))}\" height=\"{N(bh)}\" fill=\"#1b2027\"/>");
                        x += w;
                    }
                    break;
                }
                case "qr":
                {
                    double qx = e.X * k, qy = e.Y * k, qs = e.Size * k;
                    const int n = 25;
                    var cell = qs / n;
                    var rnd = new Random(Seed(e.Data ?? "qr"));
                    for (var i = 0; i < n; i++)
                        for (var j = 0; j < n; j++)
                        {
                            var inFinder = (i < 8 && j < 8) || (i < 8 && j >= n - 8) || (i >= n - 8 && j < 8);
                            if (inFinder) continue;
                            if (rnd.NextDouble() > 0.53)
                                b.Append($"<rect x=\"{N(qx + j * cell)}\" y=\"{N(qy + i * cell)}\" width=\"{N(cell + .4)}\" height=\"{N(cell + .4)}\" fill=\"#1b2027\"/>");
                        }
                    foreach (var (oi, oj) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
                        for (var a = 0; a < 7; a++)
                            for (var c = 0; c < 7; c++)
                                if (a == 0 || a == 6 || c == 0 || c == 6 || (a is >= 2 and <= 4 && c is >= 2 and <= 4))
                                    b.Append($"<rect x=\"{N(qx + (oj + c) * cell)}\" y=\"{N(qy + (oi + a) * cell)}\" width=\"{N(cell + .4)}\" height=\"{N(cell + .4)}\" fill=\"#1b2027\"/>");
                    break;
                }
                case "line":
                    b.Append($"<line x1=\"{N(e.X1 * k)}\" y1=\"{N(e.Y1 * k)}\" x2=\"{N(e.X2 * k)}\" y2=\"{N(e.Y2 * k)}\" stroke=\"#1b2027\" stroke-width=\"1\"/>");
                    break;
            }
        }
        b.Append("</svg>");
        return b.ToString();
    }

    private static string N(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    private static int Seed(string s) { var h = 17; foreach (var c in s) h = h * 31 + c; return h; }

    private static string H(string s) => (s ?? "")
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;");

    private const string Css = @"
:root{--ink:#22252a;--dim:#5b6572;--faint:#8a93a0;--line:#dbe0e5;--line2:#eef0f2;
  --accent:#e8600f;--accent-ink:#8a3606;--canvas:#1b2027;
  --ok:#127a46;--check:#b8720a;--check-bg:#fbf1dc;--warn:#b3271d;--warn-bg:#fbeae8;}
*{box-sizing:border-box;margin:0;padding:0}
body{background:#eceef1;color:var(--ink);line-height:1.55;
  font-family:-apple-system,'Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif;
  font-size:15px;padding:28px 16px 80px;}
.wrap{max-width:900px;margin:0 auto}
code,.mono{font-family:'Cascadia Mono','SF Mono',Consolas,monospace}
.doc-head{background:#fff;border:1px solid var(--line);border-radius:8px;padding:24px 26px;margin-bottom:22px}
.kicker{font:700 11px/1 'Cascadia Mono',Consolas,monospace;letter-spacing:.14em;color:var(--accent-ink);text-transform:uppercase}
h1{font-size:1.5rem;font-weight:700;margin:.5rem 0 .4rem;letter-spacing:-.01em}
.meta{font:400 12px/1.5 'Cascadia Mono',Consolas,monospace;color:var(--faint)}
.intro{margin-top:12px;font-size:13px;color:var(--dim)}
.intro b{color:var(--ink)}
.label{background:#fff;border:1px solid var(--line);border-radius:8px;padding:20px 22px;margin-bottom:20px}
.label-head{display:flex;gap:18px;align-items:stretch;padding-bottom:16px;border-bottom:1px solid var(--line2);margin-bottom:16px;flex-wrap:wrap}
.preview{flex:1 1 320px;min-width:0;display:flex;align-items:center;justify-content:center;
  background:#e9ecef;border:1px solid #b9c1c9;border-radius:4px;padding:10px;max-height:200px;overflow:hidden}
.preview svg{display:block;max-width:100%;max-height:176px;width:auto;height:auto}
.label-info{flex:0 0 auto;display:flex;flex-direction:column;gap:8px;justify-content:center}
.label-info h2{font-size:1.05rem;font-weight:700}
.label-info h2 span{color:var(--faint);font-weight:400;font-size:.85em}
.dims{font:400 11px/1 'Cascadia Mono',Consolas,monospace;color:var(--faint)}
.stats{display:flex;gap:20px;margin-top:4px}
.stat{display:flex;flex-direction:column;font-size:10px;color:var(--faint);text-transform:uppercase;letter-spacing:.03em}
.stat b{font:700 16px/1.1 'Cascadia Mono',Consolas,monospace;color:var(--ink);text-transform:none;letter-spacing:0}
.stat.conf-high b{color:var(--ok)}.stat.conf-mid b{color:var(--check)}.stat.conf-low b{color:var(--warn)}
.log{list-style:none;position:relative}
.log::before{content:'';position:absolute;left:15px;top:6px;bottom:18px;width:2px;background:#b9c1c9}
.step{position:relative;padding-left:46px;padding-bottom:14px}
.marker{position:absolute;left:0;top:0;width:32px;height:32px;border-radius:50%;display:flex;
  align-items:center;justify-content:center;background:#fff;border:2px solid #b9c1c9;
  font:700 12px/1 'Cascadia Mono',Consolas,monospace;color:var(--faint)}
.step.s-warn .marker{border-color:var(--warn);color:var(--warn)}
.step.s-check .marker{border-color:var(--check);color:var(--check)}
.card{border:1px solid var(--line);border-radius:6px;padding:13px 16px}
.card-head{display:flex;justify-content:space-between;align-items:center;gap:10px}
.eyebrow{font:700 9.5px/1 'Cascadia Mono',Consolas,monospace;letter-spacing:.12em;color:var(--faint)}
.pill{font:700 9.5px/1 'Cascadia Mono',Consolas,monospace;letter-spacing:.05em;text-transform:uppercase;
  padding:3px 7px;border-radius:3px}
.pill.auto{background:#eef1f3;color:var(--dim)}
.pill.check{background:var(--check-bg);color:var(--check)}
.pill.warn{background:var(--warn-bg);color:var(--warn)}
.card h3{font-size:.95rem;font-weight:700;margin:6px 0 5px}
.did{font-size:12.5px;color:var(--dim)}
.evidence{margin:11px 0;background:var(--canvas);color:#d6e0e6;border-radius:3px;padding:10px 12px;
  font:400 11.5px/1.7 'Cascadia Mono',Consolas,monospace;white-space:pre-wrap;word-break:break-word;overflow-x:auto}
.evidence .src{display:block;font-size:9.5px;letter-spacing:.08em;text-transform:uppercase;color:#7d93a0;margin-bottom:5px}
.tablewrap{overflow-x:auto;margin:11px 0 2px}
table{width:100%;border-collapse:collapse;font-size:11.5px}
th{text-align:left;padding:6px 10px;border-bottom:1px solid #b9c1c9;font-size:9.5px;text-transform:uppercase;
  letter-spacing:.05em;color:var(--faint);font-weight:700}
td{padding:6px 10px;border-bottom:1px solid var(--line2);vertical-align:top;
  font-family:'Cascadia Mono',Consolas,monospace;color:#333b44;word-break:break-word}
td:first-child{font-family:inherit;color:var(--dim)}
tr:last-child td{border-bottom:none}
.explain{font-size:12.5px;color:#333b44;margin-top:10px;max-width:66ch}
.verify{margin-top:11px;display:flex;gap:8px;padding:9px 12px;border-radius:3px;font-size:12px;
  background:var(--check-bg);border:1px solid #ead6a8}
.verify::before{content:'\2691';color:var(--check);flex:none}
.verify.s-warn{background:var(--warn-bg);border-color:#f0c4bf}
.verify.s-warn::before{content:'\25B2';color:var(--warn);font-size:10px}
.skipped ul{list-style:none;font-size:13px;color:var(--dim)}
.skipped li{padding:4px 0}
.skipped h2{font-size:1rem;margin-bottom:8px}
@media print{body{background:#fff;padding:0}.label,.doc-head{border:none;break-inside:avoid}.step{break-inside:avoid}}
";
}
