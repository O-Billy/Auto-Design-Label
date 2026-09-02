using AutoDesignLabel;
using System.Text.Json;

// Usage: AutoDesignLabel <ldm.json | pmd.pdf> <data.json> <out-dir> [dpi]
var ldmPath  = args.ElementAtOrDefault(0) ?? "B01O023.01.ldm.json";
var dataPath = args.ElementAtOrDefault(1) ?? "sample-data.json";
var outDir   = args.ElementAtOrDefault(2) ?? "out";
var dpi      = int.Parse(args.ElementAtOrDefault(3) ?? "203");

Directory.CreateDirectory(outDir);

var data = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(dataPath))!;

LdmDocument doc;
if (ldmPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
{
    var pdfBytes = File.ReadAllBytes(ldmPath);
    var srcName = Path.GetFileName(ldmPath);
    var cls = PmdClassifier.Classify(pdfBytes, srcName);
    Console.WriteLine($"Phan loai PMD: {(cls.LooksVector ? "vector" : "content-only")} - {cls.Reason}");

    if (cls.LooksVector)
    {
        doc = cls.VectorDoc!;
    }
    else
    {
        IPmdImageReader reader = new NullImageReader();
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            var wir = WindowsImageReader.TryCreate();
            if (wir is not null)
            {
                reader = wir;
                Console.WriteLine($"Doc anh nhan: ZXing (ma vach){(wir.HasOcr ? " + Windows.Media.Ocr (chu)" : " (khong co goi OCR)")}.");
            }
        }
        Console.WriteLine("Sinh LDM bang AUTO-LAYOUT (PMD content-only) ...");
        var spec = new ContentSpecExtractor(reader).Extract(pdfBytes, srcName);
        doc = AutoLayoutEngine.BuildDocument(spec, data);
    }

    var generatedLdmPath = Path.Combine(outDir, $"{doc.DocumentId}.ldm.json");
    File.WriteAllText(generatedLdmPath, JsonSerializer.Serialize(doc, LdmDocument.WriteOptions));
    Console.WriteLine($"Da sinh LDM: {generatedLdmPath} - XEM LAI truoc khi dung san xuat.");

    // "Nhat ky Auto-Design" (giai thich tung buoc dung ra nhan) - khong nam trong *.ldm.json
    // (xem LabelDef.Trace) nen ghi ra file .trace.html di kem de review / in.
    if (doc.Labels.Any(l => l.Trace.Count > 0))
    {
        var tracePath = Path.Combine(outDir, $"{doc.DocumentId}.trace.html");
        File.WriteAllText(tracePath, TraceReport.ToHtml(doc));
        Console.WriteLine($"Da sinh nhat ky: {tracePath}");
    }
}
else
{
    doc = LdmDocument.Load(ldmPath);
}

// 1) Canh bao cac diem con mo ho trong PMD - phai duoc PM tra loi truoc khi chay san xuat
foreach (var issue in doc.OpenIssues.Where(i => i.Severity == "blocker"))
    Console.WriteLine($"[BLOCKER {issue.Ref}] {issue.Text}");

// Dam bao moi {{TOKEN}} deu co gia tri de render PROOF (LDM van giu token that).
var missing = AutoLayoutEngine.EnsureRenderData(doc, data);
if (missing.Count > 0)
    Console.WriteLine($"[Canh bao] Thieu gia tri mau cho: {string.Join(", ", missing)} - PDF dang hien placeholder <TOKEN>.");

// 2) Xuat PDF de duyet artwork + do kich thuoc that
var lint = new Linter();
new PdfLabelRenderer(lint).Render(doc, data, Path.Combine(outDir, $"{doc.DocumentId}-1to1.pdf"));

// 3) Xuat ZPL: template nap 1 lan + lenh in ngan
var zpl = new ZplEmitter(dpi);
foreach (var label in doc.Labels)
{
    File.WriteAllText(Path.Combine(outDir, $"{label.Id}.template.zpl"),
                      zpl.EmitStoredFormat(label, data));
    File.WriteAllText(Path.Combine(outDir, $"{label.Id}.job.zpl"),
                      zpl.EmitPrintJob(label, data, label.Quantity));
}

lint.PrintReport();
if (lint.HasError)
{
    Console.WriteLine("Co loi bo cuc - khong duoc phat hanh template.");
    return 1;
}

// 4) Xuat .lab (CodeSoft 6) qua COM automation - template that (Free Variable song), dung cho Giai
// doan 1 (van dung CS6 de merge du lieu that + in, chi tu dong hoa buoc thiet ke). Loi rieng le o
// buoc nay khong lam hong PDF/ZPL da xuat duoc o tren nen chi canh bao, khong doi exit code.
// Dat ADL_SKIP_LAB=1 de bo qua (may khong co CS6, hoac lap nhanh khi phat trien).
if (Environment.GetEnvironmentVariable("ADL_SKIP_LAB") == "1")
{
    Console.WriteLine("Bo qua buoc .lab (ADL_SKIP_LAB=1).");
    Console.WriteLine($"Xong. Xem {outDir}/");
    return 0;
}
var labGen = new LabFileGenerator();
foreach (var label in doc.Labels.Where(l => l.RequiresAutoDesign))
{
    var labPath = Path.Combine(outDir, $"{label.Id}.lab");
    var labResult = labGen.Generate(label, doc, data, labPath);
    Console.WriteLine(labResult.Success
        ? $"Da xuat .lab: {labPath}"
        : $"[Canh bao] Khong xuat duoc .lab cho '{label.Id}': {labResult.ErrorMessage}");
}

Console.WriteLine($"Xong. Xem {outDir}/");
return 0;
