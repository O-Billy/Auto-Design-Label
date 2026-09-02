using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AutoDesignLabel;

/// <summary>
/// Tu dong cai dat MOI font (*.ttf/*.otf) tim thay - ca font da nhung san trong assembly (xem
/// AutoDesignLabel.csproj) LAN font trong thu muc Font/ tren dia (ke ca font MOI THEM VAO SAU nay,
/// khong can build lai) - lam font Windows THAT cho nguoi dung hien tai neu chua co, khong can
/// quyen Administrator (dung co che "install for current user" cua Windows 10 1809+: copy vao thu
/// muc Fonts rieng cua user va dang ky duoi HKCU, khong dung C:\Windows\Fonts).
///
/// LY DO CAN THIET: CS6 (COM automation, xem LabFileGenerator.cs) chi biet ve font qua TEN
/// ("JioType Light"/"JioType Medium") - neu font that chua duoc cai, CS6 TU DONG THAY THE bang 1
/// font Windows khac, va font thay the nay phu thuoc vao trang thai cache "lan dung gan nhat" cua
/// CS6 (USER.INI [TXTFONT]) - cache nay bi thay doi boi BAT KY tuong tac nao voi giao dien CS6.
/// Khi font thay the doi, metric ascent/kich thuoc doi theo, gay vo layout hang loat - da xac nhan
/// thuc te lap lai nhieu lan trong qua trinh phat trien. Cai font that mot lan loai bo hoan toan
/// su phu thuoc bap benh nay.
///
/// Idempotent va an toan: kiem tra file dich da ton tai truoc khi cai (bo qua neu da cai roi, dung
/// yeu cau "moi lan chay kiem tra, da cai thi bo qua"), khong nem loi neu that bai (vd may khong
/// phai Windows, hoac loi ghi file/registry hiem gap) - chi la buoc chuan bi tot hon cho chat luong
/// .lab, khong phai dieu kien bat buoc de Generate() chay duoc.
/// </summary>
[SupportedOSPlatform("windows")]
public static class FontInstaller
{
    private static readonly string[] FontExtensions = { ".ttf", ".otf" };

    private const string PerUserFontsRegistryKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int AddFontResourceW(string lpFileName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeoutW(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    private const int HwndBroadcast = 0xffff;
    private const uint WmFontChange = 0x001D;

    // Dang ky font qua P/Invoke truc tiep vao advapi32.dll (thay vi goi NuGet Microsoft.Win32.Registry
    // - moi truong build hien khong co mang de tai goi moi) - chi can 3 ham toi thieu cho 1 lan
    // RegCreateKeyEx + RegSetValueEx + RegCloseKey duoi HKEY_CURRENT_USER.
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegCreateKeyExW(
        IntPtr hKey, string lpSubKey, int reserved, string? lpClass, int dwOptions,
        int samDesired, IntPtr lpSecurityAttributes, out IntPtr phkResult, out int lpdwDisposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegSetValueExW(
        IntPtr hKey, string lpValueName, int reserved, int dwType, string lpData, int cbData);

    [DllImport("advapi32.dll")]
    private static extern int RegCloseKey(IntPtr hKey);

    private static readonly IntPtr HKeyCurrentUser = unchecked((IntPtr)(int)0x80000001);
    private const int KeySetValue = 0x0002;
    private const int RegOptionNonVolatile = 0;
    private const int RegSzType = 1;

    private static void SetPerUserFontRegistryValue(string valueName, string fileName)
    {
        var rc = RegCreateKeyExW(HKeyCurrentUser, PerUserFontsRegistryKey, 0, null, RegOptionNonVolatile,
            KeySetValue, IntPtr.Zero, out var hKey, out _);
        if (rc != 0) throw new InvalidOperationException($"RegCreateKeyExW that bai (rc={rc}).");
        try
        {
            // REG_SZ can ky tu NUL ket thuc, cbData tinh theo byte (UTF-16 = 2 byte/ky tu).
            var data = fileName + "\0";
            rc = RegSetValueExW(hKey, valueName, 0, RegSzType, data, data.Length * 2);
            if (rc != 0) throw new InvalidOperationException($"RegSetValueExW that bai (rc={rc}).");
        }
        finally
        {
            RegCloseKey(hKey);
        }
    }

    /// <summary>Cai moi font ma chua co tren may - goi o dau LabFileGenerator.Generate() (xem noi
    /// goi) de moi lan xuat .lab deu tu dam bao truoc. Quet CA HAI nguon: (1) font nhung san trong
    /// assembly (luon co du chay tu ban publish nao), (2) font dang ton tai TRONG THU MUC Font/ TREN
    /// DIA canh repo (tim tu dong tu vi tri assembly dang chay, khong hardcode duong dan) - dung yeu
    /// cau "copy them 1 font vao thu muc thi tu dong cai, khong chi rieng JioType". 1 file trung ten
    /// o ca 2 nguon chi duoc cai 1 lan (uu tien nguon nao quet truoc, khong quan trong vi noi dung
    /// giong nhau). Best-effort: nuot moi loi, chi ghi log ra Console (khong chan luong Generate()
    /// chinh).</summary>
    public static void EnsureInstalled()
    {
        if (!OperatingSystem.IsWindows()) return;

        string perUserFontsDir;
        try
        {
            perUserFontsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows", "Fonts");
            Directory.CreateDirectory(perUserFontsDir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FontInstaller] Khong the chuan bi thu muc font ca nhan: {ex.Message}");
            return;
        }

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Nguon 1: font nhung san trong assembly (LogicalName trong .csproj chinh la ten file, vd
        // "JioType-Light.ttf") - luon co bat ke chay tu source hay tu ban publish/deploy nao.
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!HasFontExtension(resourceName)) continue;
            InstallOneSafe(resourceName, perUserFontsDir,
                () => assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException($"Khong doc duoc resource '{resourceName}'."));
            installed.Add(resourceName);
        }

        // Nguon 2: quet TRUC TIEP thu muc Font/ tren dia (neu tim thay canh repo) - bat duoc font
        // MOI ai do them vao SAU khi build (vd copy them 1 file .ttf) ma khong can rebuild project,
        // dung dung yeu cau nguoi dung: "tôi copy thêm 1 font thì sẽ tự động cài".
        var fontDir = FindFontDirectory();
        if (fontDir is not null)
        {
            foreach (var filePath in Directory.EnumerateFiles(fontDir))
            {
                var fileName = Path.GetFileName(filePath);
                if (!HasFontExtension(fileName) || installed.Contains(fileName)) continue;
                InstallOneSafe(fileName, perUserFontsDir, () => File.OpenRead(filePath));
            }
        }
    }

    private static bool HasFontExtension(string name) =>
        FontExtensions.Any(ext => name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tim thu muc "Font" nam canh cay thu muc repo (AutoDesignLabel-csharp/Font) bang cach
    /// do nguoc tu vi tri assembly dang chay (AppContext.BaseDirectory, vd
    /// ".../AutoDesignLabel.Web/bin/Debug/net8.0/") len toi da 8 cap thu muc cha, o moi cap kiem tra
    /// co thu muc con ten "Font" chua it nhat 1 file font hay khong - khong hardcode duong dan tuyet
    /// doi (khac nhau giua CLI/Web, giua may dev khac nhau), dung duoc du chay tu nguon nao trong
    /// repo. Tra null (bo qua nguon 2, van con nguon 1 tu assembly) neu khong tim thay - vd truong
    /// hop publish doc lap khong mang theo thu muc Font/ goc.</summary>
    private static string? FindFontDirectory()
    {
        try
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Font");
                if (Directory.Exists(candidate) &&
                    Directory.EnumerateFiles(candidate).Any(f => HasFontExtension(f)))
                    return candidate;
            }
        }
        catch
        {
            // Best-effort - loi do (vd khong co quyen doc 1 cap thu muc nao do) chi bo qua nguon nay.
        }
        return null;
    }

    private static void InstallOneSafe(string fileName, string perUserFontsDir, Func<Stream> openSource)
    {
        try
        {
            InstallOne(fileName, perUserFontsDir, openSource);
        }
        catch (Exception ex)
        {
            // Best-effort per font - 1 file loi khong duoc lam hong cac file con lai.
            Console.WriteLine($"[FontInstaller] Khong cai duoc '{fileName}': {ex.Message}");
        }
    }

    private static void InstallOne(string fileName, string perUserFontsDir, Func<Stream> openSource)
    {
        var targetPath = Path.Combine(perUserFontsDir, fileName);

        byte[] sourceBytes;
        using (var sourceStream = openSource())
        using (var ms = new MemoryStream())
        {
            sourceStream.CopyTo(ms);
            sourceBytes = ms.ToArray();
        }
        // Sua loi "chu thuong tu dong bi in nghieng" tan goc TRONG chinh file font (xem giai thich
        // day du tren PatchTypographicFamilyNames) - ap dung cho MOI file, khong rieng JioType, vi
        // day la 1 loi thiet ke font pho bien (nhieu bo font co cac trong luong Light/Medium/Bold...
        // chia se 1 "Typographic Family" duoc dat qua NameID 16/17).
        var patchedBytes = PatchTypographicFamilyNames(sourceBytes);

        // Ghi file neu chua co / khac noi dung (tu phuc hoi may da cai ban font cu chua patch).
        var fileWasWritten = false;
        if (!File.Exists(targetPath) ||
            !File.ReadAllBytes(targetPath).AsSpan().SequenceEqual(patchedBytes))
        {
            File.WriteAllBytes(targetPath, patchedBytes);
            fileWasWritten = true;
        }

        // LUON dam bao dang ky registry ĐUNG - ke ca khi file da ton tai san (KHONG return som o
        // tren nua). Ly do (xac nhan qua thuc nghiem voi CS6 truc tiep): ban FontInstaller cu ghi
        // sai 2 diem, moi diem deu du de "JioType Light" ra chu NGHIENG:
        //   (1) TEN value lay tu PrivateFontCollection.Families[0].Name - khong on dinh: bo JioType
        //       co bang 'name' loi (JioType-Light.ttf va JioType-LightItalic.ttf CUNG NameID-1 =
        //       "JioType Light", chi khac NameID-2 Regular/Italic) nen nhieu file cung do ve value
        //       "JioType Light (TrueType)", ban Italic ghi de ban Regular (last-write-wins).
        //   (2) Ghi TEN FILE TRAN ("JioType-Light.ttf") thay vi DUONG DAN DAY DU - value trong
        //       HKCU\...\Fonts giai chieu ten tran theo C:\Windows\Fonts (font he thong), KHONG
        //       phai thu muc font per-user - nen entry treo, GDI/CS6 khong nap duoc -> thay the.
        // Sua: ten value = NameID-1 (family) + NameID-2 (subfamily) doc THANG tu file .ttf da patch
        // (moi face 1 ten rieng: "JioType Light (TrueType)" cho Regular, "JioType Light Italic
        // (TrueType)" cho Italic...), va DATA = DUONG DAN DAY DU toi file per-user. Idempotent nen
        // moi lan chay deu tu sua lai cho dung du file da nam san.
        var family = ReadWindowsName(patchedBytes, 1) ?? Path.GetFileNameWithoutExtension(fileName);
        var subfamily = NormalizeSubfamily(ReadWindowsName(patchedBytes, 2));
        var typeLabel = fileName.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ? "OpenType" : "TrueType";
        var valueName = subfamily.Length == 0
            ? $"{family} ({typeLabel})"
            : $"{family} {subfamily} ({typeLabel})";

        SetPerUserFontRegistryValue(valueName, targetPath); // DUONG DAN DAY DU, khong phai ten file

        // Nap ngay cho tien trinh hien tai + bao cac tien trinh khac (CS6 dang mo san, neu co) load
        // lai danh sach font - tien trinh CS6 MOI (spawn sau thoi diem nay, truong hop pho bien nhat
        // vi LabFileGenerator luon tao Application instance moi) se tu thay font qua registry, khong
        // can buoc nay, nhung lam them cho chac va khong hai gi.
        AddFontResourceW(targetPath);
        SendMessageTimeoutW((IntPtr)HwndBroadcast, WmFontChange, IntPtr.Zero, IntPtr.Zero, 0, 1000, out _);

        if (fileWasWritten)
            Console.WriteLine($"[FontInstaller] Da cai '{valueName}' <- {fileName} cho nguoi dung hien tai.");
    }

    /// <summary>"Regular"/"Normal"/rong -> "" (khong hau to style trong ten value registry, dung quy
    /// uoc Windows: "Arial (TrueType)" cho Regular, "Arial Bold (TrueType)" cho Bold...). Cac gia tri
    /// khac ("Bold", "Italic", "Bold Italic", "Light"...) giu nguyen.</summary>
    private static string NormalizeSubfamily(string? sub)
    {
        sub = sub?.Trim() ?? "";
        return sub.Equals("Regular", StringComparison.OrdinalIgnoreCase)
            || sub.Equals("Normal", StringComparison.OrdinalIgnoreCase)
            || sub.Equals("Book", StringComparison.OrdinalIgnoreCase)
            ? "" : sub;
    }

    /// <summary>Doc 1 chuoi tu bang 'name' cua file font cho platform Windows (platformID=3, chuoi
    /// UTF-16BE). Tra ve null neu khong tim thay hoac file khong dung cau truc mong doi. Uu tien
    /// ban tieng Anh (languageID 0x0409) neu co nhieu ban ngon ngu.</summary>
    private static string? ReadWindowsName(byte[] bytes, ushort wantNameId)
    {
        try
        {
            ushort U16(int p) => (ushort)((bytes[p] << 8) | bytes[p + 1]);
            uint U32(int p) => (uint)((bytes[p] << 24) | (bytes[p + 1] << 16) | (bytes[p + 2] << 8) | bytes[p + 3]);

            var numTables = U16(4);
            var nameOffset = 0u;
            for (var i = 0; i < numTables; i++)
            {
                var recPos = 12 + i * 16;
                if (System.Text.Encoding.ASCII.GetString(bytes, recPos, 4) == "name")
                {
                    nameOffset = U32(recPos + 8);
                    break;
                }
            }
            if (nameOffset == 0) return null;

            var count = U16((int)nameOffset + 2);
            var storage = (int)nameOffset + U16((int)nameOffset + 4);
            string? fallback = null;
            for (var i = 0; i < count; i++)
            {
                var recPos = (int)nameOffset + 6 + i * 12;
                var platformId = U16(recPos);
                var languageId = U16(recPos + 4);
                var nameId = U16(recPos + 6);
                if (platformId != 3 || nameId != wantNameId) continue;
                var len = U16(recPos + 8);
                var off = U16(recPos + 10);
                var s = System.Text.Encoding.BigEndianUnicode.GetString(bytes, storage + off, len).Trim();
                if (s.Length == 0) continue;
                if (languageId == 0x0409) return s; // English (US) - uu tien
                fallback ??= s;
            }
            return fallback;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>NGUYEN NHAN GOC cua loi "chu thuong (vd JioType Light) tu dong bi in nghieng du
    /// Font.Italic da duoc gan False qua COM" (da xac nhan qua thuc nghiem thuc te, sua duoc 100%):
    /// cac file .ttf trong bo JioType Light/Medium/Bold... chia se CHUNG 1 "Typographic Family Name"
    /// (OpenType NameID 16 = "JioType") du moi trong luong co Family Name RIENG o NameID 1 ("JioType
    /// Light", "JioType Medium"...). Windows/GDI, khi thay nhieu file cung 1 Typographic Family
    /// (NameID 16) nhung GDI co dinh chi ho tro toi da 4 "kieu" cho 1 family (Regular/Bold/Italic/
    /// BoldItalic), phai "nhet" cac trong luong THUA (Light, Medium...) vao 1 trong 4 o do theo mot
    /// heuristic noi bo cua he dieu hanh - va "Light" thuong bi xep nham vao o "Italic". Day la 1
    /// loi/gioi han THIET KE PHO BIEN cua nhieu bo font co nhieu trong luong (khong rieng JioType),
    /// KHONG lien quan gi den USER.INI/cache CS6 hay lop retry SetPropVerified da them truoc do (ca
    /// hai deu vo hieu voi loi nay vi no nam ngay trong CACH WINDOWS NAP FONT, truoc ca khi CS6 kip
    /// dieu khien qua COM).
    ///
    /// Cach sua: VO HIEU HOA cac NameRecord NameID=16/17 (Typographic Family/Subfamily, platform
    /// Windows=3) trong bang 'name' cua file .ttf bang cach doi ma NameID cua chung sang 1 gia tri
    /// khong duoc OpenType dinh nghia (255) - Windows/GDI se ROI VE dung Family Name legacy (NameID
    /// 1) rieng cho tung file, khong con nhom chung "JioType" nua nen khong con can "nhet" trong
    /// luong vao 4 o gia han. Chi doi 2 byte/record (chinh ma NameID), KHONG dung/doi do dai bat ky
    /// chuoi nao - an toan, khong can dich chuyen offset cua cac bang khac trong file. Ap dung cho
    /// MOI file font duoc cai (khong chi JioType) vi day la van de thiet ke font pho bien.</summary>
    private static byte[] PatchTypographicFamilyNames(byte[] original)
    {
        var bytes = (byte[])original.Clone();
        try
        {
            ushort ReadU16At(int pos) => (ushort)((bytes[pos] << 8) | bytes[pos + 1]);
            uint ReadU32At(int pos) => (uint)((bytes[pos] << 24) | (bytes[pos + 1] << 16) | (bytes[pos + 2] << 8) | bytes[pos + 3]);

            var numTables = ReadU16At(4);
            var nameOffset = 0u;
            for (var i = 0; i < numTables; i++)
            {
                var recPos = 12 + i * 16;
                var tag = System.Text.Encoding.ASCII.GetString(bytes, recPos, 4);
                if (tag == "name") { nameOffset = ReadU32At(recPos + 8); break; }
            }
            if (nameOffset == 0) return bytes; // Khong phai OpenType/TrueType hop le - tra nguyen ban, khong dung sai.

            var count = ReadU16At((int)nameOffset + 2);
            for (var i = 0; i < count; i++)
            {
                var recPos = (int)nameOffset + 6 + i * 12;
                var platformId = ReadU16At(recPos);
                var nameId = ReadU16At(recPos + 6);
                if (platformId == 3 && (nameId == 16 || nameId == 17))
                {
                    bytes[recPos + 6] = 0x00;
                    bytes[recPos + 7] = 0xFF;
                }
            }
        }
        catch
        {
            return original; // File bat thuong/khong doc duoc dung cau truc mong doi - an toan hon la tra ban goc.
        }
        return bytes;
    }
}
