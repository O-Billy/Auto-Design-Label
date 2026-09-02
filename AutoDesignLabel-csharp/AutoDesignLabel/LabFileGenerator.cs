using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AutoDesignLabel;

public sealed record LabResult(bool Success, string? ErrorMessage);

/// <summary>
/// Xuat LDM ra file .lab (CodeSoft 6) bang COM automation - dieu khien chinh CS6 tao document,
/// them Text/Barcode/QR/Line roi SaveAs, thay vi tu dung dinh dang nhi phan doc quyen cua TEKLYNX.
///
/// Don vi noi bo CS6 la 1/1000 inch (mil) - xac nhan thuc nghiem qua Format.LabelWidth/Height mac
/// dinh (1575 -> 40.00mm, 787 -> 20.00mm): units = round(mm * 1000 / 25.4).
///
/// Cac truong {{TOKEN}} trong LDM duoc giu SONG duoi dang Free Variable that trong CS6 (khong bake
/// thanh gia tri tinh) - file .lab sinh ra la TEMPLATE, dung Filler/Database that trong CS6 de nhap
/// du lieu that va in, giong quy trinh thu cong hien tai (chi tu dong hoa buoc THIET KE).
///
/// Gioi han da biet (xem ke hoach trien khai): du lieu QR (XML composite nhieu token) va cac dong
/// mo rong tu element 'repeat' duoc bake thanh text tinh (dung gia tri mau) - CS6 khong co co che
/// bind nhieu bien vao 1 Barcode.Value nhu co the lam voi nhieu Text object rieng cho truong hop
/// text hon hop (xem AddMixedContentAsSeparateTexts).
/// </summary>
public sealed class LabFileGenerator
{
    private const double MilPerMm = 1000.0 / 25.4;
    private static int Mil(double mm) => (int)Math.Round(mm * MilPerMm);

    // Ty le ascent (mep tren -> baseline) danh rieng cho CS6 - xem giai thich chi tiet tai
    // CreateTextObject. Do thuc nghiem tren may hien tai (chua cai JioType lam font Windows).
    private const double AscentRatio = 0.72;

    // Hang enum cua Lppx2 (CodeSoft 6 automation) - lay tu dump reflection Lppx2.tlb, chi giu cac
    // gia tri thuc su dung de tranh phai ship interop assembly generate boi tlbimp.
    private static class Lppx
    {
        public const int AnchorTopLeft = 1;
        public const int SymbologyCode128 = 67;
        public const int SymbologyQrcode = 123;
        // enumViewMode - dieu khien canvas thiet ke cua CS6 hien TEN bien (mac dinh,
        // lppxViewModeName=1, hien "MAC"/"EAN"/"RSN" trong khung xanh) hay GIA TRI hien tai cua bien
        // (lppxViewModeValue=3, hien "012345678901" nhu du lieu mau) cho cac doi tuong bind qua
        // VariableName. Nguoi dung can .lab mo ra hien san gia tri mau (giong buoc review PDF) de
        // de doi chieu, van giu duoc bien song (Filler/Database van hoat dong binh thuong khi merge
        // du lieu that sau nay - ViewMode chi la che do HIEN THI luc thiet ke, khong anh huong du
        // lieu thuc te duoc bind).
        public const int ViewModeValue = 3;
    }

    private const int StaTimeoutMs = 30_000;
    private const int MaxAttempts = 3;

    // CS6 automation on KHONG chiu duoc NHIEU app instance chay CUNG LUC (xac nhan qua thuc nghiem
    // rieng: mo nhieu Document/Application dong thoi de nhau gay treo/vo layout hon han khi chay
    // tuan tu) - server Web co the nhan NHIEU request "Export .lab" gan nhau (nguoi dung bam nhieu
    // lan, hoac nhieu tab). Khoa tinh (static) nay serialize TOAN BO Generate() trong pham vi 1 tien
    // trinh .NET, dam bao tai 1 thoi diem chi co DUY NHAT 1 Application COM dang duoc dieu khien -
    // request sau xep hang cho thay vi chay song song va lam tang nguy co treo.
    private static readonly SemaphoreSlim GenerationLock = new(1, 1);

    /// <summary>Xac nhan qua thuc nghiem: CS6 (Lppx2/CodeSoft2 COM) thinh thoang treo vo thoi han
    /// (dung ~100% CPU lien tuc trong nhieu phut, khong phai deadlock cho dialog - co the lien quan
    /// kiem tra license CodeMeter khi khoi dong lap lai) - khong deterministictheo nhan hay noi dung
    /// cu the nao. Vi khong the huy an toan 1 Thread .NET dang ket qua trong loi goi COM, buoc phai
    /// gioi han thoi gian cho va KILL THANG tien trinh lppa.exe neu qua han, roi thu lai tu dau (moi
    /// lan thanh cong deu hoan tat trong vai giay, nen retry la chien luoc thuc te hon la co gang
    /// xac dinh nguyen nhan goc trong noi bo CS6/CodeMeter).</summary>
    public LabResult Generate(
        LabelDef label, LdmDocument doc, IReadOnlyDictionary<string, string> sampleData,
        string outputPath, bool visible = false)
    {
        // Kiem tra/cai font JioType that TRUOC khi dieu khien CS6 - xem FontInstaller.cs de biet ly
        // do (font thay the khong on dinh la nguon goc chinh cua loi vo layout hang loat da gap
        // nhieu lan). Idempotent + best-effort, khong chan Generate() neu that bai.
        FontInstaller.EnsureInstalled();

        GenerationLock.Wait();
        try
        {
            LabResult last = new(false, "Khong ro nguyen nhan.");
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                last = GenerateOnce(label, doc, sampleData, outputPath, visible);
                if (last.Success) return last;
                // Cho he thong (CodeMeter/OS) mot khoang nghi truoc khi thu lai - kill ngay roi khoi
                // dong lai lien tuc co the khien tinh trang treo lap lai thay vi tu phuc hoi.
                if (attempt < MaxAttempts) Thread.Sleep(3_000);
            }
            return last;
        }
        finally
        {
            GenerationLock.Release();
        }
    }

    private static LabResult GenerateOnce(
        LabelDef label, LdmDocument doc, IReadOnlyDictionary<string, string> sampleData,
        string outputPath, bool visible)
    {
        // Chup danh sach tien trinh "lppa" TRUOC khi khoi dong - lam O DAY (ben ngoai luong STA),
        // KHONG chi dua vao pid ma luong STA tu bao cao qua onProcessStarted. Ly do: xac nhan qua
        // thuc nghiem thuc te la Activator.CreateInstance() TU NO cung co the treo (khong chi cac
        // buoc SAU nhu Documents.Add) - neu treo ngay tai do, luong STA khong bao gio chay toi dong
        // ghi pid, khien pid mai la 0 va nhanh kill-khi-timeout ben duoi khong kill duoc gi ca, de
        // sot tien trinh lppa.exe treo VINH VIEN (tich luy dan qua moi lan retry, gop phan lam he
        // thong ngay cang khong on dinh - da quan sat thuc te tren server). So sanh danh sach TRUOC
        // va SAU timeout, kill TAT CA tien trinh "lppa" MOI xuat hien (khong co trong pidsBefore) la
        // cach dang tin cay hon nhieu, khong phu thuoc luong STA co kip bao cao hay khong.
        var pidsBefore = Process.GetProcessesByName("lppa").Select(p => p.Id).ToHashSet();

        LabResult? result = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = GenerateOnStaThread(label, doc, sampleData, outputPath, visible, _ => { });
            }
            catch (Exception ex)
            {
                result = new LabResult(false, $"Loi khi dieu khien CodeSoft 6: {ex.Message}");
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        if (!thread.Join(StaTimeoutMs))
        {
            foreach (var p in Process.GetProcessesByName("lppa"))
            {
                if (pidsBefore.Contains(p.Id)) continue;
                try { p.Kill(); } catch { /* co the da tu thoat giua luc kiem tra va kill */ }
            }
            thread.Join(5_000);
            return new LabResult(false, "CodeSoft 6 bi treo qua thoi gian cho - da huy tien trinh, se thu lai.");
        }
        return result ?? new LabResult(false, "Khong ro nguyen nhan - luong STA khong tra ket qua.");
    }

    private static LabResult GenerateOnStaThread(
        LabelDef label, LdmDocument doc, IReadOnlyDictionary<string, string> sampleData,
        string outputPath, bool visible, Action<int> onProcessStarted)
    {
        var appType = Type.GetTypeFromProgID("CodeSoft2.Application");
        if (appType is null)
            return new LabResult(false,
                "Khong tim thay CodeSoft 6 COM Application ('CodeSoft2.Application') tren may nay - " +
                "can cai CS6 va dang ky COM (Bin\\CSReg.exe) de dung chuc nang xuat .lab.");

        // Ghi lai PID cua tien trinh lppa.exe (CS6) MOI duoc CreateInstance mo ra - xac nhan qua
        // thuc nghiem la goi Quit() KHONG dam bao tien trinh thoat hoan toan ngay lap tuc; goi Generate
        // lien tiep (vd nhieu nhan trong 1 lan chay CLI) co the treo/loi COM ngau nhien (RPC_E_SERVERFAULT,
        // hoac hang vo thoi han) neu tien trinh CU chua don xong ma tien trinh MOI da khoi dong. Doi
        // dung PID nay thoat han truoc khi tra ket qua, force-kill neu qua thoi gian cho.
        var pidsBefore = Process.GetProcessesByName("lppa").Select(p => p.Id).ToHashSet();
        dynamic? app = null;
        dynamic? cdoc = null;
        int? newPid = null;
        try
        {
            app = Activator.CreateInstance(appType)!;
            app.Visible = visible;
            newPid = Process.GetProcessesByName("lppa")
                .Select(p => (int?)p.Id).FirstOrDefault(id => !pidsBefore.Contains(id!.Value));
            if (newPid is not null) onProcessStarted(newPid.Value);

            // QUAN TRONG: Options.MeasureSystem (Tools > Options > Display > "Unit of measurement"
            // trong giao dien CS6) KHONG CHI la don vi HIEN THI o thuoc - no con quyet dinh CS6 DIEN
            // GIAI cac gia tri so nguyen truyen qua COM (Left/Top/LabelWidth...) la mil (1/1000 inch,
            // gia dinh xuyen suot code nay qua ham Mil()) hay mm - xac nhan qua thuc nghiem: doi cai
            // dat nay sang Millimeters (vi du nguoi dung tu doi qua Options de tien xem/sua tay trong
            // CS6) lam TOAN BO vi tri Left/Top bi dien giai sai don vi, gay chong chu hang loat tren
            // ca nhan (khong lien quan gi den font/ascent - da from nham 1 lan). Ep ve lppxInch=1 O
            // DAY, moi lan truoc khi tao Document, de Generate() LUON dung don vi mil bat ke nguoi
            // dung dang de Options o che do nao cho viec xem/sua tay - khong can nguoi dung phai nho
            // doi lai truoc khi dung chuc nang xuat .lab.
            app.Options.MeasureSystem = 1; // enumMeasureSystem.lppxInch

            cdoc = app.Documents.Add(label.Id);
            cdoc.Format.LabelWidth = Mil(label.WidthMm);
            cdoc.Format.LabelHeight = Mil(label.HeightMm);

            var elements = label.Elements.Where(e => e.Type != "repeat").ToList();
            CreateFreeVariables(cdoc, elements, doc, sampleData);

            var key = 0;
            foreach (var el in elements)
                AddObject(cdoc, el, sampleData, ref key);

            // Element 'repeat' chua duoc PmdExtractor su dung cho nhan thuc te nao, nhung LDM hand-
            // authored co the co - bung bang Binder.Expand (bake gia tri mau, xem gioi han o dau
            // file). Binder.Expand tra ve CA element thuong (CUNG tham chieu voi label.Elements) LAN
            // element moi duoc clone tu Template - loc ra chi phan MOI (khong nam trong elements da
            // xu ly o tren) bang so sanh tham chieu.
            var alreadyHandled = new HashSet<Element>(elements);
            foreach (var el in Binder.Expand(label, sampleData).Where(e => !alreadyHandled.Contains(e)))
                AddObject(cdoc, el, sampleData, ref key);

            // Hien gia tri mau (khong phai ten bien) khi mo file .lab de xem/doi chieu - xem ghi
            // chu tai Lppx.ViewModeValue.
            cdoc.ViewMode = Lppx.ViewModeValue;

            var rc = (int)cdoc.SaveAs(outputPath);
            var result = rc != 0
                ? new LabResult(true, null)
                : new LabResult(false, $"CodeSoft 6 tu choi SaveAs (rc={rc}) - kiem tra duong dan '{outputPath}'.");

            // Dep dep (Close/Quit) thuc hien SAU KHI da xac dinh ket qua - da xac nhan qua thuc
            // nghiem la CS6 co the nem loi COM ("The server threw an exception", RPC_E_SERVERFAULT)
            // ngay tai Close() dù SaveAs da thanh cong va file .lab da duoc ghi day du xuong dia (rc
            // tu SaveAs la dang tin cay duy nhat). Loi dep dep khong duoc lam mat ket qua SaveAs that.
            CleanUp(app, cdoc, newPid);
            return result;
        }
        catch (Exception ex)
        {
            CleanUp(app, cdoc, newPid);
            return new LabResult(false, $"Loi khi dieu khien CodeSoft 6: {ex.Message}");
        }
        finally
        {
            if (cdoc is not null) Marshal.ReleaseComObject(cdoc);
            if (app is not null) Marshal.ReleaseComObject(app);
        }
    }

    private static void CleanUp(dynamic? app, dynamic? cdoc, int? pid)
    {
        try { cdoc?.Close(false); } catch { /* file .lab da ghi xong, loi dep dep khong quan trong */ }
        try { app?.Quit(); } catch { /* best effort - tranh sot tien trinh lppa.exe */ }
        if (pid is null) return;
        try
        {
            var proc = Process.GetProcessById(pid.Value);
            if (!proc.WaitForExit(10_000)) proc.Kill();
        }
        catch (ArgumentException) { /* tien trinh da thoat - GetProcessById nem loi, dung y muon */ }
    }

    private static void CreateFreeVariables(
        dynamic cdoc, List<Element> elements, LdmDocument doc, IReadOnlyDictionary<string, string> sampleData)
    {
        var tokens = new HashSet<string>();
        foreach (var el in elements)
        {
            if (el.Text is not null) foreach (var t in Binder.TokenNames(el.Text)) tokens.Add(t);
            // Du lieu QR (composite) bi bake tinh (xem AddObject) nen KHONG tao Free Variable cho
            // token chi xuat hien trong el.Data cua phan tu "qr" - tranh tao bien "mo coi" khong ai
            // bind toi trong CS6, gay nham lan khi nguoi dung mo Filler.
            if (el.Data is not null && el.Type != "qr")
                foreach (var t in Binder.TokenNames(el.Data)) tokens.Add(t);
        }

        foreach (var token in tokens)
        {
            var sample = doc.Fields.TryGetValue(token, out var f) && f.Sample is not null
                ? f.Sample
                : sampleData.GetValueOrDefault(token, "");
            var v = cdoc.Variables.FreeVariables.Add(token);
            v.Value = sample;
            // QUAN TRONG: Free.Length mac dinh la 25 (khong tu suy ra tu do dai Value da gan) - xac
            // nhan qua thuc nghiem COM truc tiep. CS6 dung Length nay (KHONG dung do dai thuc te cua
            // Value) de tinh kich thuoc (Width) cua moi doi tuong Barcode/Text bind qua VariableName -
            // neu bo qua, buoc "do roi hieu chinh" trong AddObject se doc nham 1 Width ao (dua tren 25
            // ky tu gia dinh) thay vi do dai that cua du lieu mau, tinh sai he so scale va lam barcode
            // to hon dung nhieu, tran de len phan tu ben canh (da xac nhan bang so do: cung 1 gia tri
            // 12 ky tu, Length mac dinh 25 cho Width=9304mil nhung set Length=12 cho dung 5012mil,
            // khop voi truong hop bind Value tinh).
            v.Length = Math.Max(1, sample.Length);
            v.FormPrompt = token;
            v.DisplayInForm = true;
        }
    }

    private static void AddObject(
        dynamic cdoc, Element el, IReadOnlyDictionary<string, string> sampleData, ref int key)
    {
        key++;
        switch (el.Type)
        {
            case "text":
            {
                var text = el.Text ?? "";
                var tokenNames = Binder.TokenNames(text).ToList();
                if (tokenNames.Count > 1 || (tokenNames.Count == 1 && text != "{{" + tokenNames[0] + "}}"))
                {
                    AddMixedContentAsSeparateTexts(cdoc, el, text, sampleData, key);
                }
                else
                {
                    var t = CreateTextObject(cdoc, el, $"T{key}", el.X);
                    if (tokenNames.Count == 1) t.VariableName = tokenNames[0];
                    else t.Value = text;
                }
                break;
            }

            case "barcode128":
            {
                var b = cdoc.DocObjects.Barcodes.Add($"B{key}");
                b.Symbology = Lppx.SymbologyCode128;
                // CS6 mac dinh tu ve THEM 1 dong "human readable text" (gia tri ma hoa, phong to)
                // ngay duoi vach vach (HRPosition=1 mac dinh - xac nhan qua thuc nghiem qua
                // Lppx2.tlb, khong thay tai lieu chinh thuc nao mo ta) - PDF/ZPL renderer khong co
                // khai niem nay (LDM da co san 1 text element rieng hien gia tri canh nhan, vd "MAC
                // ID : 012345678901"), nen dong tu sinh nay CHI la ban sao thua, de LEN chinh label
                // do (da xac nhan qua anh chup man hinh: "012345678901" to hien lai, de len "MAC ID
                // :"). Tat han bang HRPosition=0.
                b.HRPosition = 0;
                SetPropVerified(b, "Left", Mil(el.X));
                SetPropVerified(b, "Top", Mil(el.Y));
                b.BarHeight = Mil(el.Height);
                var nominalNbw = Mil(Linter.MinXDimMm);
                b.NarrowBarWidth = nominalNbw;
                BindSingleValue(b, el.Data ?? "");
                // Bo dem/co dinh dang cua bo ma hoa Code128 rieng cua CS6 khac dang ke so voi PDF/ZPL
                // renderer (xac nhan qua thuc nghiem: cung 1 gia tri co the ma hoa nhieu module hon
                // han, ~4x trong thu nghiem thuc te) - neu giu san "khong nho hon nguong doc duoc",
                // barcode se GAN NHU LUON tran rong hang tram mm ra ngoai nhan (vi CS6 hau nhu luon
                // can co nho hon nguong de khop kich thuoc thiet ke goc, nen san se vo hieu hoa buoc
                // hieu chinh trong da so truong hop - da xac nhan qua anh chup man hinh thuc te). Vi
                // day la file THIET KE de xem lai/chinh trong CS6 (khong truc tiep dieu khien may in),
                // uu tien khop dung kich thuoc/vi tri thiet ke goc (tranh layout vo/de len phan tu
                // khac) hon la giu nguyen nguong doc-tay-ly-tuong - nguoi dung co the tu dieu chinh
                // NarrowBarWidth truoc khi dua vao san xuat that neu can. Chi giu san ky thuat toi
                // thieu 1 mil de tranh gia tri 0/am.
                if (el.Width > 0)
                {
                    var actualMil = (int)b.Width;
                    if (actualMil > 0)
                    {
                        var scale = (double)Mil(el.Width) / actualMil;
                        b.NarrowBarWidth = Math.Max(1, (int)Math.Round(nominalNbw * scale));
                    }
                }
                break;
            }

            case "qr":
            {
                var q = cdoc.DocObjects.Barcodes.Add($"Q{key}");
                q.Symbology = Lppx.SymbologyQrcode;
                SetPropVerified(q, "Left", Mil(el.X));
                SetPropVerified(q, "Top", Mil(el.Y));
                // Code2D.set khong hoat dong qua COM (nem "Unable to write read-only property" -
                // xac nhan qua thuc nghiem) - phai sua TRUC TIEP tren sub-object tra ve tu get_Code2D
                // (tham chieu con, khong can gan lai property cha), giong cach Font hoat dong.
                var nominalModule = Mil(Linter.MinQrModuleMm);
                q.Code2D.ModuleX = nominalModule;
                q.Code2D.ModuleY = nominalModule;
                // Gioi han da biet: du lieu QR la XML composite nhieu token, CS6 Barcode.Value
                // khong co co che mixed literal+variable nhu Text - bake thanh text tinh bang gia
                // tri mau (giong noi dung dang hien trong preview PDF). Xem doc-comment dau file.
                q.Value = el.Data is null ? "" : SafeBindForPreview(el.Data, sampleData);
                // Cung logic do-roi-hieu-chinh nhu barcode128 - Width mac dinh cua CS6 co the khac
                // xa el.Size du dinh. Uu tien khop dung kich thuoc thiet ke (xem giai thich chi tiet
                // o barcode128 phia tren) hon la giu san nguong doc-ly-tuong MinQrModuleMm - chi giu
                // san ky thuat toi thieu 1 mil.
                if (el.Size > 0)
                {
                    var actualMil = (int)q.Width;
                    if (actualMil > 0)
                    {
                        var scale = (double)Mil(el.Size) / actualMil;
                        var corrected = Math.Max(1, (int)Math.Round(nominalModule * scale));
                        q.Code2D.ModuleX = corrected;
                        q.Code2D.ModuleY = corrected;
                    }
                }
                break;
            }

            case "line":
                cdoc.DocObjects.Shapes.AddLine(Mil(el.X1), Mil(el.Y1), Mil(el.X2), Mil(el.Y2));
                break;

            case "image":
                // Chua co asset anh that (PDF renderer cung chi ve khung placeholder) - giu tuong
                // duong bang 1 hinh chu nhat rong.
                cdoc.DocObjects.Shapes.AddRectangle(
                    Mil(el.X), Mil(el.Y), Mil(el.X + el.Width), Mil(el.Y + el.Height));
                break;
        }
    }

    /// <summary>Gan 1 property qua COM BANG Type.InvokeMember (KHONG qua toan tu "obj.Prop = value"
    /// cua "dynamic" C#) roi DOC LAI de xac nhan gia tri thuc su duoc ap dung, thu lai neu khong
    /// khop. 2 van de rieng biet da xac nhan qua thuc nghiem deu can cach nay:
    ///   1) So nguyen (Left/Top...): duoi tai automation nang (nhieu doi tuong Text/Barcode tao lien
    ///      tuc trong 1 phien), CS6 doi luc "nuot" 1 lenh gan property ma KHONG nem loi COM nao ca -
    ///      gia tri cu/mac dinh (thuong la 0) van con nguyen, gay CHONG CHU len phan tu khac. Khong
    ///      xay ra deu dan nen retry giai quyet duoc.
    ///   2) Bool (Font.Italic): KHAC HAN - khong phai flaky, ma toan tu "dynamic" gan (font.Italic =
    ///      false; qua Microsoft.CSharp.RuntimeBinder) LUON THAT BAI 100% CAC LAN (xac nhan qua thuc
    ///      nghiem: van con True sau ca 5 lan thu lai qua duong nay) - trong khi InvokeMember voi
    ///      BindingFlags.SetProperty THANH CONG NGAY LAN DAU. Nghi van: RuntimeBinder co the dung
    ///      DISPATCH_PROPERTYPUTREF thay vi DISPATCH_PROPERTYPUT cho VARIANT_BOOL qua IDispatch late-
    ///      binding, hoac 1 khac biet marshaling khac rieng cho kieu bool - InvokeMember tranh duoc
    ///      hoan toan bang cach khong di qua RuntimeBinder.</summary>
    private static void SetPropVerified<T>(dynamic obj, string propName, T value, int maxAttempts = 4)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            obj.GetType().InvokeMember(propName, System.Reflection.BindingFlags.SetProperty,
                null, obj, new object?[] { value });
            var actual = (T)obj.GetType().InvokeMember(propName,
                System.Reflection.BindingFlags.GetProperty, null, obj, null);
            if (Equals(actual, value)) return;
            if (attempt < maxAttempts) Thread.Sleep(30);
        }
    }

    /// <summary>Lop phong thu THU HAI cho Bold/Italic/Underline/Strikethrough - xem giai thich chi
    /// tiet tai noi goi trong CreateTextObject. Doc lai FRESH tu t.Font (KHONG dung lai bien "font"
    /// cu) sau khi da gan qua SetPropVerified + t.Font = font; - neu van con sai (quan sat qua thuc
    /// nghiem: rieng buoc gan lai "t.Font = font;" cung co the bi "nuot" khong deu dan giua nhieu
    /// Text object tao lien tiep trong 1 label thuc te, cung lop loi COM duoi tai nhu Left/Top,
    /// khong lo ra khi thu nghiem 1 doi tuong don le), lap lai TOAN BO chu ky doc-sua-gan-lai (khong
    /// chi sua tren 1 ban sao "cache" roi quen gan lai t.Font) toi da 5 lan.</summary>
    private static void EnsureFontFlags(dynamic t)
    {
        for (var round = 1; round <= 5; round++)
        {
            dynamic check = t.Font;
            if (!(bool)check.Bold && !(bool)check.Italic &&
                !(bool)check.Underline && !(bool)check.Strikethrough)
                return; // Da dung ca 4 co - khong can lam gi them.

            SetPropVerified(check, "Bold", false);
            SetPropVerified(check, "Italic", false);
            SetPropVerified(check, "Underline", false);
            SetPropVerified(check, "Strikethrough", false);
            t.Font = check; // QUAN TRONG: buoc gan lai nay chinh la buoc hay bi "nuot" - lap lai neu can.
        }
    }

    private static void BindSingleValue(dynamic barcodeOrQr, string data)
    {
        var tokenNames = Binder.TokenNames(data).ToList();
        if (tokenNames.Count == 1 && data == "{{" + tokenNames[0] + "}}")
            barcodeOrQr.VariableName = tokenNames[0];
        else
            barcodeOrQr.Value = data;
    }

    // Dung khi bake noi dung co token thanh text tinh (QR, cac dong 'repeat') - khong nem loi neu
    // thieu du lieu mau, chi thay the token bang gia tri mau tuong ung (giong noi dung dang hien
    // trong preview PDF); giu nguyen {{TOKEN}} neu khong co gia tri mau de nguoi dung con thay can
    // dien gi.
    private static string SafeBindForPreview(string text, IReadOnlyDictionary<string, string> sampleData) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"\{\{(\w+)\}\}",
            m => sampleData.GetValueOrDefault(m.Groups[1].Value, m.Value));

    private static dynamic CreateTextObject(dynamic cdoc, Element el, string objKey, double xMm)
    {
        var t = cdoc.DocObjects.Texts.Add(objKey);
        // Text moi tao mac dinh co khung rat hep (~1.8mm, xac nhan qua thuc nghiem) va WordWrap=1 -
        // voi noi dung dai hon khung, CS6 ngat XUONG DONG lien tuc (tung ky tu 1 dong, nhin nhu chu
        // viet doc). LDM cua chung ta khong co khai niem "khung co dinh gioi han chieu rong" cho
        // text (moi phan tu la 1 dong don, tu do rong theo noi dung, giong PDF/ZPL renderer) - tat
        // WordWrap de khung tu mo rong theo 1 dong duy nhat, dung ngu nghia LDM.
        t.WordWrap = 0;
        var font = t.Font;
        // Do dam/nhat da duoc chon qua TEN FONT rieng (JioType Medium vs JioType Light) - KHONG
        // duoc ep them Bold=true cho ban Medium (da tung lam, gay loi net chu bi ve doi/nhoe do
        // CS6/GDI gia lap dam chong len 1 font ten da la ban dam san - xac nhan qua screenshot
        // nguoi dung bao loi). Quy uoc nay khac PdfLabelRenderer (dung 1 family "JioType" + style
        // Bold/Regular) vi o day ta chon family theo ten rieng cho tung do dam.
        // Nhan Auto (FontFamily != null) khong theo spec typographic PMD -> dung font trung tinh
        // Arial co san tren moi may Windows/CS6. Do dam van chon qua TEN FAMILY ("Arial Bold") giong
        // quy uoc JioType o duoi, KHONG ep co Bold (xem giai thich ve viec ep 4 co ben duoi).
        font.Name = el.FontFamily is not null
            ? (el.Font == "medium" ? "Arial Bold" : "Arial")
            : (el.Font == "medium" ? "JioType Medium" : "JioType Light");
        font.Size = el.Size;
        // QUAN TRONG: gan cac thuoc tinh BOOL (Bold/Italic/Underline/Strikethrough) cua Font PHAI
        // qua SetPropVerified (Type.InvokeMember), KHONG duoc dung toan tu "font.Italic = false" cua
        // "dynamic" C# nhu truoc day - da xac nhan qua thuc nghiem: doi voi rieng kieu bool, toan tu
        // gan cua "dynamic" (qua Microsoft.CSharp.RuntimeBinder) THAT BAI 100% CAC LAN mot cach am
        // tham (khong nem loi, nhung gia tri khong doi) - vi du Text moi tao ke thua Italic=True tu
        // trang thai CS6 (khong lien quan gi USER.INI, da loai tru qua thuc nghiem), va "font.Italic
        // = false" khong bao gio "dinh" duoc (xac nhan qua anh chup man hinh nguoi dung: toan bo chu
        // hien nghieng sai voi PMD). InvokeMember tranh hoan toan duong RuntimeBinder nay va thanh
        // cong ngay lan dau. Ep ca 4 co (khong chi Italic) de khong con phu thuoc bat ky trang thai
        // ke thua nao cua CS6 - dung nghia "sua triet de cho moi truong hop".
        SetPropVerified(font, "Bold", false);
        SetPropVerified(font, "Italic", false);
        SetPropVerified(font, "Underline", false);
        SetPropVerified(font, "Strikethrough", false);
        t.Font = font;
        // KIEM TRA LAI tren chinh sub-object t.Font (khong phai bien "font" trung gian) SAU KHI gan
        // - xac nhan qua thuc nghiem: voi 1 doi tuong Text tao rieng le, sua tren "font" roi gan lai
        // la du, nhung trong 1 chuoi NHIEU doi tuong Text tao lien tiep (11+ trong 1 label thuc te),
        // 1 vai doi tuong van bi tra ve Italic=True du da qua SetPropVerified - hien tuong khong deu
        // dan theo tung doi tuong (khong phai luon la doi tuong dau/cuoi) cho thay day la loi COM
        // duoi tai giong het lop loi da gap voi Left/Top, chi la o "Italic" khong the phat hien qua
        // 1 lan tao doc lap ma chi lo ro khi tao NHIEU doi tuong lien tuc. Sua truc tiep tren t.Font
        // (khong qua bien trung gian) neu van con sai la lop phong thu THU HAI, dam bao "sua triet
        // de cho MOI truong hop" nhu yeu cau, khong chi truong hop don gian da kiem thu rieng.
        EnsureFontFlags(t);
        t.AnchorPoint = Lppx.AnchorTopLeft;
        SetPropVerified(t, "Left", Mil(xMm));
        // QUAN TRONG: el.Y trong LDM la BASELINE cua dong chu (PdfLabelRenderer dung
        // XStringFormats.BaseLineLeft, ZplEmitter tru cung cong thuc nay truoc khi phat ^FO) - nhung
        // CS6 AnchorTopLeft dinh vi theo MEP TREN khung chu, khong phai baseline. Neu gan thang
        // Top=el.Y (nhu truoc day), 2 doan text CUNG el.Y nhung khac co chu (vd "1" 12pt canh "N
        // Device +" 6pt, dung de gia lap "1" so mu "N") se bi neo mep tren GIONG NHAU thay vi baseline
        // giong nhau - doan chu nho bi day len nhin nhu bi dinh len dau doan chu to (da xac nhan qua
        // anh chup man hinh nguoi dung: "N" hien thanh so mu dinh vao "1"). Tru ascent de quy doi
        // baseline -> mep tren, dam bao MOI text trong .lab thang hang dung nhu ban xem truoc PDF/ZPL.
        //
        // Ty le ascent dung o day (AscentRatio, KHONG phai 0.72 nhu PdfLabelRenderer/ZplEmitter) da
        // duoc do lai THUC NGHIEM rieng cho CS6: JioType Light/Medium chua duoc CAI DAT nhu FONT
        // WINDOWS tren may chay COM automation (chi ton tai o dang file .ttf trong repo, xem gioi han
        // #3 trong ke hoach trien khai) nen CS6 tu dong thay the bang 1 font Windows khac de ve - ty le
        // ascent/kich thuoc cua font thay the nay KHAC voi JioType that (von la co so cho hang so 0.72
        // dung trong PDF/ZPL). Da kiem chung bang cach: dat 2 text "1"(12pt)/"N Device +"(6pt) cung 1
        // el.Y, chup man hinh CS6 that, do khoang lech pixel giua 2 baseline, quy doi ve mm qua ty le
        // px/mm tu chinh vi tri X cua 2 phan tu (chenh lech ty le thuan voi kich thuoc chu, dung cho
        // suy luan). SAU KHI cai dat JioType lam font Windows that (xem prerequisite trong ke hoach),
        // ty le nay se can do lai va co the quay ve dung chung 0.72 voi PDF/ZPL.
        SetPropVerified(t, "Top", Mil(el.Y - el.Size * AscentRatio / 2.835));
        return t;
    }

    // Ket hop text tinh + token trong CUNG 1 dong (vd "Pallet No.: {{PALLET_SEQ}}") - da thu dung
    // Text.AppendString/AppendVariable (API rich-text cua CS6 de ghep nhieu doan vao 1 Text object)
    // nhung CS6 tu nem loi COM phia server (RPC_E_SERVERFAULT) khi goi qua automation du dung chu ky
    // - co the la gioi han/loi rieng cua CS6 khi dieu khien tu ben ngoai UI. Giai phap chac chan hon:
    // tach thanh NHIEU Text object rieng biet dat canh nhau, moi doan (tinh hoac gan Variable) la 1
    // object doc lap - giong cach LDM/PDF renderer da xu ly cac truong nhan+gia tri khac tu truoc.
    // Vi tri doan sau uoc luong tho theo do rong ky tu (CS6 khong co API do be rong chu qua COM) -
    // nguoi dung xem lai/chinh trong CS6 nhu moi phan tu auto-design khac neu lech.
    private static void AddMixedContentAsSeparateTexts(
        dynamic cdoc, Element el, string source, IReadOnlyDictionary<string, string> sampleData, int key)
    {
        var pos = 0;
        var xMm = el.X;
        var segIndex = 0;
        foreach (System.Text.RegularExpressions.Match m in
                 System.Text.RegularExpressions.Regex.Matches(source, @"\{\{(\w+)\}\}"))
        {
            if (m.Index > pos)
            {
                var literal = source[pos..m.Index];
                CreateTextObject(cdoc, el, $"T{key}_{segIndex++}", xMm).Value = literal;
                xMm += EstimateWidthMm(literal, el.Size);
            }
            var token = m.Groups[1].Value;
            CreateTextObject(cdoc, el, $"T{key}_{segIndex++}", xMm).VariableName = token;
            xMm += EstimateWidthMm(sampleData.GetValueOrDefault(token, token), el.Size);
            pos = m.Index + m.Length;
        }
        if (pos < source.Length)
            CreateTextObject(cdoc, el, $"T{key}_{segIndex++}", xMm).Value = source[pos..];
    }

    private static double EstimateWidthMm(string text, double sizePt) =>
        text.Length * sizePt * 0.5 * 25.4 / 72.0;
}
