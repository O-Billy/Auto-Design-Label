# Auto Design Label — Build & Deploy lên IIS

Tài liệu này hướng dẫn build **AutoDesignLabel.Web** (giao diện Blazor Server) và triển khai lên
IIS trên máy chủ Windows nội bộ. Phần tổng quan kiến trúc / pipeline chi tiết hơn xem `CLAUDE.md`.

---

## 1. Cấu trúc dự án

Chỉ liệt kê những thư mục / file quan trọng cần biết khi build và deploy.

```
Auto-Design-Label/                         ← gốc repo
├── CLAUDE.md                               ← tài liệu kiến trúc tổng thể
├── README.md                               ← file này (build + deploy IIS)
├── render_pdf.py                           ← bản tham chiếu Python (ReportLab): chỉ PDF + lint,
│                                             KHÔNG ZPL/.lab/PMD. Dùng để đối chiếu logic render.
├── B01G017.03_LBL-*.pdf, B01O023.01-*.pdf  ← PMD mẫu dạng vector (test PmdExtractor)
├── SE PMD/                                 ← PMD mẫu dạng ảnh/PowerPoint (test nhánh content-only)
├── sample-data.json                        ← dữ liệu {{TOKEN}} mẫu để render proof
│
└── AutoDesignLabel-csharp/                 ← toàn bộ mã C# (bản chính)
    ├── AutoDesignLabel.sln                 ← solution — ĐỪNG build cả sln (.Wpf làm lỗi MSB4057)
    │
    ├── Font/                               ← 6 file JioType-*.ttf. Được NHÚNG vào assembly lõi
    │                                          lúc biên dịch → KHÔNG copy rời ra publish.
    │
    ├── publish/AutoDesignLabel.Web/        ← ĐÍCH của `dotnet publish` (xem mục 4). Thư mục copy
    │                                          lên máy chủ IIS. ~34 MB.
    │
    ├── AutoDesignLabel/                    ★★★ THƯ VIỆN LÕI — mọi logic pipeline ở đây
    │   ├── Program.cs                      ← điểm vào CLI (dotnet run). Trình tự cố định:
    │   │                                      load data → phân loại PMD → render PDF → ZPL → .lab
    │   ├── Ldm.cs                          ← ĐỊNH NGHĨA MODEL LDM (LdmDocument/LabelDef/Element),
    │   │                                      Binder (thay {{TOKEN}}, bung 'repeat'), TraceStep
    │   │
    │   │   ── Nạp PMD → LDM (2 nhánh) ──
    │   ├── PmdClassifier.cs                ← quyết định PMD là "vector" hay "content-only"
    │   ├── PmdExtractor.cs                 ← NHÁNH VECTOR: đọc toạ độ thật từ PDF (PdfPig).
    │   │                                      File lớn nhất; cũng dựng "Trace Log" từng bước.
    │   ├── ContentSpec.cs                  ← model danh sách trường cho nhánh content-only
    │   ├── ContentSpecExtractor.cs         ← NHÁNH ẢNH: đọc text layer PPT → danh sách trường
    │   ├── AutoLayoutEngine.cs             ← NHÁNH ẢNH: tự dựng layout mặc định từ ContentSpec
    │   ├── IPmdImageReader.cs              ← interface đọc ảnh (barcode/OCR)
    │   ├── WindowsImageReader.cs           ← hiện thực: ZXing giải barcode + Windows.Media.Ocr
    │   │                                      → LÝ DO cả 4 project target net8.0-windows10.0.19041
    │   │
    │   │   ── Xuất ──
    │   ├── PdfLabelRenderer.cs             ← vẽ PDF 1:1 (PdfSharpCore); gọi Linter trong lúc vẽ
    │   ├── Linter.cs                       ← luật in ấn: X-dim barcode, quiet-zone QR, tràn lề,
    │   │                                      chồng lấn (bản Python trong render_pdf.py phải khớp)
    │   ├── ZplEmitter.cs                   ← sinh ZPL (mã máy in nhiệt Zebra): template + job
    │   ├── LabFileGenerator.cs             ← sinh .lab qua COM CodeSoft 6 (cần Windows + CS6)
    │   ├── TraceReport.cs                  ← xuất "Trace Log" ra HTML tự chứa + vẽ SVG nhãn 1:1
    │   │                                      (dùng chung cho web popup và file .trace.html)
    │   │
    │   │   ── Hỗ trợ ──
    │   ├── FontInstaller.cs                ← cài JioType lên Windows (HKCU, không cần admin) để
    │   │                                      CS6 không thay font lung tung. Idempotent, không throw.
    │   ├── JioTypeFontResolver.cs          ← nạp font nhúng cho PdfSharpCore
    │   └── PrinterClient.cs                ← gửi ZPL qua TCP 9100 (chưa gắn vào Program.cs)
    │
    ├── AutoDesignLabel.Web/                ★★★ GIAO DIỆN BLAZOR SERVER — bọc thư viện lõi
    │   ├── Program.cs                      ← khởi tạo web: Razor Pages + Server Blazor;
    │   │                                      NÂNG message SignalR lên 50 MB (upload PMD);
    │   │                                      KHÔNG HTTPS redirect; endpoint /download-file/{token}
    │   ├── FileDownloads.cs                ← "hộp thư" token GUID ngắn hạn → file tạm để tải về
    │   ├── App.razor, _Imports.razor       ← khung Blazor
    │   ├── appsettings.json                ← cấu hình logging (production)
    │   ├── Properties/launchSettings.json  ← CHỈ dùng khi chạy dev (dotnet run); IIS bỏ qua
    │   │
    │   ├── Pages/
    │   │   ├── Index.razor                 ★ TOÀN BỘ MÀN HÌNH: upload → phân loại → sửa spec →
    │   │   │                                  preview PDF từng nhãn → xuất .lab/ZPL (~1000 dòng)
    │   │   ├── _Host.cshtml, _Layout.cshtml ← trang HTML gốc; nạp bootstrap + site.css + blazor.js
    │   │   └── Error.cshtml                ← trang lỗi production
    │   │
    │   ├── Shared/
    │   │   ├── AutoDesignLog.razor         ← popup "Trace Log" (nút ở footer trang review)
    │   │   ├── LoadingOverlay.razor        ← overlay tiến trình khi đang xử lý
    │   │   └── MainLayout.razor            ← layout khung
    │   │
    │   └── wwwroot/                        ← tĩnh: css/bootstrap, css/site.css (design system
    │                                          "MES console"), css/open-iconic, favicon.ico
    │
    └── AutoDesignLabel.Wpf/                ← KHUNG RỖNG, chưa làm, KHÔNG build được. Bỏ qua.
```

### Luồng dữ liệu

```
PMD.pdf ──> PmdClassifier ──┬─ vector ────> PmdExtractor ─────────┐
                            │                                     ├──> LdmDocument (Ldm.cs)
                            └─ ảnh ──> ContentSpecExtractor ──>    │        │
                                       AutoLayoutEngine ──────────┘        │
                                                                          ▼
                     ┌────────────────────────┬─────────────────┬──────────────────┐
                     ▼                        ▼                 ▼                  ▼
              PdfLabelRenderer            ZplEmitter      LabFileGenerator     TraceReport
              (+ Linter)                    │             (COM CodeSoft 6)         │
                     │                      │                   │                  │
                  *-1to1.pdf         *.template.zpl          *.lab          *.trace.html
                                     *.job.zpl
```

- **Web** (`Index.razor`) gọi đúng các lớp lõi này, chỉ khác là hiển thị kết quả trên trình duyệt
  thay vì ghi ra thư mục `out/`.
- **CLI** (`Program.cs`) làm y hệt nhưng ghi file ra đĩa và trả exit code (≠ 0 nếu linter có lỗi
  bố cục — tràn lề hoặc chồng lấn).

### Vì sao toàn bộ target `net8.0-windows10.0.19041.0`

`WindowsImageReader` dùng `Windows.Media.Ocr` + `Windows.Graphics.Imaging` (WinRT) để đọc barcode
và OCR từ mockup dạng ảnh — chỉ có trên Windows. `FontInstaller` (đăng ký font qua registry) và
`LabFileGenerator` (COM CodeSoft 6) cũng Windows-only. `render_pdf.py` là bản tham chiếu đa nền tảng.

---

## 2. Tóm tắt nhanh (TL;DR)

Trên máy dev (đã có .NET 8 SDK):

```powershell
cd d:\2026\dev\Auto-Design-Label\AutoDesignLabel-csharp
dotnet publish AutoDesignLabel.Web\AutoDesignLabel.Web.csproj -c Release -o publish\AutoDesignLabel.Web
```

Trên máy chủ IIS (làm **một lần**):

1. Cài **ASP.NET Core Hosting Bundle 8.x** → chạy `iisreset`.
2. Bật tính năng Windows **WebSocket Protocol** cho IIS.
3. Tạo Application Pool: **No Managed Code**, và Site trỏ vào thư mục publish.

Mỗi lần cập nhật: `Stop` site → copy đè thư mục publish → `Start` site.

Thư mục publish đã được sinh sẵn tại:
`AutoDesignLabel-csharp\publish\AutoDesignLabel.Web\` (kết quả của lần build gần nhất).

---

## 3. Điều kiện máy chủ

| Hạng mục | Yêu cầu |
|---|---|
| Hệ điều hành | Windows Server 2019/2022 hoặc Windows 10/11 (build ≥ 10.0.19041). App target `net8.0-windows10.0.19041.0`. |
| IIS | Đã cài, kèm **WebSocket Protocol** (Blazor Server dùng SignalR qua WebSocket). |
| .NET | **ASP.NET Core Hosting Bundle 8.0.x** (gồm .NET Runtime + ASP.NET Core Runtime + module `AspNetCoreModuleV2`). Không cần cài SDK trên máy chủ. |
| (Tùy chọn) OCR | Gói ngôn ngữ OCR của Windows — chỉ cần cho nhánh PMD "content-only" (PMD dạng ảnh). Không có gói này thì việc giải mã **barcode vẫn chạy**, chỉ mất phần đọc chữ bằng OCR. |
| (Tùy chọn) CodeSoft 6 | Chỉ cần nếu muốn nút **Export .lab** hoạt động ngay trên máy chủ. Xem mục 9. |

Kiểm tra Hosting Bundle đã cài đúng:

```powershell
dotnet --list-runtimes
# Phải thấy cả hai dòng phiên bản 8.0.x:
#   Microsoft.NETCore.App 8.0.x
#   Microsoft.AspNetCore.App 8.0.x
& "$env:windir\system32\inetsrv\appcmd.exe" list modules | Select-String AspNetCoreModuleV2
```

> **Lưu ý thứ tự cài:** nếu cài Hosting Bundle **trước** khi cài IIS, phải cài lại Hosting Bundle
> (hoặc chạy `dotnet-hosting-8.x.x-win.exe /repair`) để nó đăng ký module vào IIS.

---

## 4. Build / Publish trên máy dev

### 4.1. Yêu cầu

- .NET 8 SDK (bản dùng để phát triển: `8.0.400`). Kiểm tra: `dotnet --info`.
- Không cần Visual Studio; chỉ cần `dotnet` CLI.

### 4.2. Lệnh publish

Chạy từ thư mục `AutoDesignLabel-csharp`:

```powershell
cd d:\2026\dev\Auto-Design-Label\AutoDesignLabel-csharp

# Xóa output cũ cho chắc
if (Test-Path publish\AutoDesignLabel.Web) { Remove-Item publish\AutoDesignLabel.Web -Recurse -Force }

dotnet publish AutoDesignLabel.Web\AutoDesignLabel.Web.csproj `
    -c Release `
    -o publish\AutoDesignLabel.Web
```

Ý nghĩa các tham số:

| Tham số | Ý nghĩa |
|---|---|
| `dotnet publish` | Biên dịch **rồi gom** assembly + toàn bộ thư viện phụ thuộc + `wwwroot` + `web.config` vào một thư mục hoàn chỉnh, sẵn sàng copy đi deploy (khác `dotnet build` chỉ ra `.dll` để chạy dev). |
| `AutoDesignLabel.Web.csproj` | Chỉ publish project web; .NET tự kéo theo project lõi `AutoDesignLabel` vì được tham chiếu. |
| `-c Release` | Cấu hình Release (tối ưu, bỏ mã debug). |
| `-o publish\AutoDesignLabel.Web` | Thư mục đích. Không có `-o` thì output nằm sâu trong `bin\Release\...\publish\`. |

Kết quả nằm trong `publish\AutoDesignLabel.Web\` (~34 MB), gồm:

- `AutoDesignLabel.Web.dll` — assembly ứng dụng (IIS/ANCM nạp file này để chạy).
- `AutoDesignLabel.dll` — thư viện lõi (pipeline PMD → LDM → PDF/ZPL/.lab), font JioType nhúng bên trong.
- `web.config` — cấu hình cho `AspNetCoreModuleV2`; `dotnet publish` **tự sinh**, IIS đọc file này.
- `wwwroot\` — CSS/tài nguyên tĩnh.
- `AutoDesignLabel.Web.runtimeconfig.json` / `*.deps.json` — khai báo runtime + danh sách DLL.
- ~18 DLL bên thứ ba: `UglyToad.PdfPig.*`, `PdfSharpCore`, `zxing`, `Microsoft.Windows.SDK.NET` +
  `WinRT.Runtime` (OCR), `System.Drawing.Common`…
- `runtimes\win\` — bản DLL native cho Windows.
- **KHÔNG có thư mục `Font\`** — 6 file JioType `.ttf` đã nhúng vào `AutoDesignLabel.dll` lúc biên
  dịch (khai báo `<EmbeddedResource>` trong `.csproj`). Đây là chủ ý, không phải thiếu file.
- `AutoDesignLabel.exe` + vài `*.ldm.json` / `sample-data.json` bị kéo theo từ project lõi (vô hại,
  có thể xóa nếu muốn gọn).

Kiểu publish là **framework-dependent** (mặc định): thư mục publish **không** chứa .NET runtime →
máy chủ phải cài Hosting Bundle. Ưu điểm: nhẹ, và runtime được vá bảo mật theo máy chủ.

### 4.3. Các lưu ý khi build

- **Publish thẳng project `.Web`, đừng publish/build cả solution.** `dotnet build AutoDesignLabel.sln`
  sẽ **fail** ở project rỗng `AutoDesignLabel.Wpf` (`MSB4057`) — lỗi này có sẵn, không ảnh hưởng
  hai project kia.
- Các cảnh báo `MSB4011` (import SDK trùng), `NETSDK1086`, `CS0618`, `CS1668 (VC98)` khi publish là
  **vô hại** — publish vẫn `succeeded`.
- Nếu app dev đang chạy (`dotnet run`), nó khóa file trong `bin\Debug\`. Việc này **không** cản
  `dotnet publish` (publish dùng cấu hình `Release`, thư mục khác). Nếu vẫn gặp lỗi khóa file, tắt
  app dev rồi publish lại.

### 4.4. Kiểm chứng nhanh bản build (trên máy dev)

```powershell
cd publish\AutoDesignLabel.Web
$env:ASPNETCORE_URLS = "http://127.0.0.1:8199"
dotnet .\AutoDesignLabel.Web.dll
```

Mở `http://127.0.0.1:8199/` — phải thấy trang review, tiêu đề tab "Auto Design Label", không lỗi
trong console. `Ctrl+C` để dừng. (Bản build hiện tại đã được kiểm chứng theo cách này.)

---

## 5. Cấu hình IIS (làm một lần)

### 5.1. Cài các tính năng Windows cần thiết

**Windows Server** (PowerShell admin):

```powershell
Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets -IncludeManagementTools
```

**Windows 10/11** (PowerShell admin):

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName `
  IIS-WebServerRole, IIS-WebServer, IIS-WebSockets, IIS-ManagementConsole -All
```

### 5.2. Cài ASP.NET Core Hosting Bundle

Tải `dotnet-hosting-8.0.x-win.exe` từ trang download .NET 8 (mục **ASP.NET Core Runtime →
Hosting Bundle**), cài xong chạy:

```powershell
net stop was /y
net start w3svc
# hoặc đơn giản: iisreset
```

Hosting Bundle gộp 3 thứ: (a) .NET Runtime 8, (b) ASP.NET Core Runtime 8, (c) module
`AspNetCoreModuleV2` cắm vào IIS. **Không cần cài SDK** trên máy chủ.

### 5.3. Tạo Application Pool

IIS Manager → **Application Pools** → *Add Application Pool*:

| Thuộc tính | Giá trị |
|---|---|
| Name | `AutoDesignLabel` |
| .NET CLR version | **No Managed Code** |
| Managed pipeline mode | Integrated |

Sau đó *Advanced Settings* của pool:

- **Load User Profile** = `True` — **bắt buộc**. `FontInstaller` ghi font vào registry
  `HKCU` và cần thư mục `%TEMP%` của identity; nếu không load profile, việc cài font và ghi
  file tạm (`.lab` / `.zpl` / `.ldm.json` để tải về) sẽ hỏng.
- **Identity**: xem mục 9 để chọn cho phù hợp với nhu cầu `.lab`. Mặc định
  `ApplicationPoolIdentity` là đủ cho PDF/ZPL.
- **Start Mode** = `AlwaysRunning` (tùy chọn — giữ app khởi động sẵn, tránh chậm lần đầu).
- **Idle Time-out (minutes)** = `0` (tùy chọn — không tắt app khi rảnh; quan trọng vì phiên
  Blazor Server là stateful, app tắt thì người đang thao tác mất phiên).

### 5.4. Tạo Site

1. Copy thư mục `publish\AutoDesignLabel.Web\` lên máy chủ, ví dụ `C:\inetpub\AutoDesignLabel\`.
2. IIS Manager → **Sites** → *Add Website*:
   - Site name: `AutoDesignLabel`
   - Application pool: `AutoDesignLabel`
   - Physical path: `C:\inetpub\AutoDesignLabel`
   - Binding: `http`, port ví dụ `8080` (hoặc host name nội bộ như `label.congty.local`).
3. **Triển khai ở gốc site**, không đặt làm sub-application (`/adl`). App không cấu hình
   `UsePathBase`, để dưới đường dẫn con sẽ hỏng `_framework/blazor.server.js` và hub SignalR.

> **HTTP, không HTTPS:** đây là công cụ nội bộ LAN, `Program.cs` cố tình **không** bật
> HTTPS redirect. Nếu chính sách công ty bắt buộc HTTPS, thêm binding `https` với chứng chỉ
> nội bộ và cho phép cả hai — **đừng** thêm `UseHttpsRedirection` vào code.

### 5.5. Phân quyền thư mục

App ghi file tạm qua `Path.GetTempFileName()` (vào `%TEMP%` của identity, đã có nhờ Load User
Profile). Với thư mục site chỉ cần quyền đọc; ANCM ghi log vào `.\logs\` nên cấp quyền ghi cho
thư mục con đó:

```powershell
$acct = "IIS AppPool\AutoDesignLabel"   # đổi nếu dùng identity khác
icacls "C:\inetpub\AutoDesignLabel" /grant "${acct}:(OI)(CI)(RX)" /T
New-Item "C:\inetpub\AutoDesignLabel\logs" -ItemType Directory -Force
icacls "C:\inetpub\AutoDesignLabel\logs" /grant "${acct}:(OI)(CI)(M)"
```

---

## 6. Cấu hình ứng dụng

### 6.1. Môi trường

Publish sinh sẵn `web.config` với `ASPNETCORE_ENVIRONMENT` **không đặt** → app chạy ở
`Production` (đúng ý muốn: ẩn chi tiết lỗi, dùng `/Error`). Nếu cần bật lỗi chi tiết tạm thời,
sửa `web.config` trên máy chủ, thêm vào trong thẻ `<aspNetCore>`:

```xml
<aspNetCore processPath="dotnet" arguments=".\AutoDesignLabel.Web.dll" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="inprocess">
  <environmentVariables>
    <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Development" />
  </environmentVariables>
</aspNetCore>
```

> Mỗi lần `dotnet publish` sẽ **ghi đè `web.config`**. Nếu cần chỉnh cố định, sửa **trên máy
> chủ sau khi copy**, hoặc đặt biến ở cấp Application Pool (IIS → *Configuration Editor* →
> `system.webServer/aspNetCore`).

### 6.2. Kích thước upload PMD

- `Program.cs` đã nâng giới hạn message SignalR lên **50 MB**
  (`MaximumReceiveMessageSize`) — đủ cho PMD PDF vài MB. Upload trong Blazor Server đi qua
  mạch SignalR (WebSocket) chứ không phải POST multipart, nên giới hạn
  `maxAllowedContentLength` của IIS **không áp dụng** cho bước upload.
- Endpoint tải file kết quả (`/download-file/{token}`) chỉ trả file nhỏ (.lab/.zpl/.ldm.json),
  không cần chỉnh gì.
- Nếu về sau PMD lớn hơn 50 MB: sửa số trong `Program.cs` rồi publish lại.

### 6.3. `hostingModel` — in-process vs out-of-process

Mặc định `inprocess` (nhanh hơn, chạy trong `w3wp.exe`). Nếu gặp trục trặc với **OCR
(`Windows.Media.Ocr`)** hoặc **COM CodeSoft** khi chạy in-process, đổi trong `web.config`:

```xml
hostingModel="outofprocess"
```

Khi đó IIS chạy `dotnet.exe` như tiến trình con và proxy request — cô lập tốt hơn cho WinRT/COM,
đổi lại chậm hơn một chút.

---

## 7. Kiểm tra sau khi deploy

1. Trình duyệt trong LAN → `http://<tên-máy-chủ>:8080/`.
2. Trang review hiện lên (không lỗi 500.x). Nếu lỗi:
   - `500.19` → sai `web.config` hoặc thiếu `AspNetCoreModuleV2` (cài lại Hosting Bundle).
   - `500.30` → app crash lúc khởi động; bật `stdoutLogEnabled="true"` rồi xem `.\logs\stdout_*.log`,
     hoặc xem **Event Viewer → Windows Logs → Application** (source `IIS AspNetCore Module V2`).
   - `502.5` → sai phiên bản runtime / `hostingModel`.
3. Upload một file PMD mẫu (ví dụ `B01G017.03_LBL-00XX_...pdf` ở gốc repo) → xem preview PDF 1:1
   render được → mở **Trace Log** → thử **Export ZPL** (không cần CodeSoft).
4. Kiểm tra WebSocket: DevTools → Network → phải có kết nối `_blazor?id=...` ở trạng thái
   `101 Switching Protocols`. Nếu là polling / bị rớt liên tục → chưa bật WebSocket Protocol
   trong IIS.

---

## 8. Cập nhật phiên bản mới

```powershell
# --- máy dev ---
cd d:\2026\dev\Auto-Design-Label\AutoDesignLabel-csharp
dotnet publish AutoDesignLabel.Web\AutoDesignLabel.Web.csproj -c Release -o publish\AutoDesignLabel.Web

# --- máy chủ (PowerShell admin) ---
Import-Module WebAdministration
Stop-Website  -Name AutoDesignLabel
Stop-WebAppPool -Name AutoDesignLabel      # để nhả khóa DLL
# copy đè: publish\AutoDesignLabel.Web\*  ->  C:\inetpub\AutoDesignLabel\
Start-WebAppPool -Name AutoDesignLabel
Start-Website  -Name AutoDesignLabel
```

Cách "sạch" hơn (không cần stop): tạo file rỗng `app_offline.htm` trong thư mục site trước khi
copy, xóa đi sau khi copy xong — ANCM sẽ tự tắt app, nhả khóa, rồi khởi động lại.

> Copy đè sẽ ghi đè `web.config` — nếu đã chỉnh tay `web.config` trên máy chủ (mục 6), lưu lại
> bản chỉnh và áp lại sau khi copy.

---

## 9. Tính năng `.lab` (CodeSoft 6) khi chạy trên IIS — QUAN TRỌNG

`LabFileGenerator` điều khiển **CodeSoft 6 qua COM automation**
(`Type.GetTypeFromProgID("CodeSoft2.Application")`). Đây là tự động hóa một **ứng dụng desktop**,
nên chạy dưới tiến trình IIS (Session 0, không có desktop tương tác) là **không ổn định**.

**Lựa chọn triển khai:**

| Phương án | Cách làm | Đánh đổi |
|---|---|---|
| **A. Không xuất `.lab` trên máy chủ** (khuyến nghị nếu máy chủ không cài CS6) | Chấp nhận nút *Export .lab* báo lỗi; người dùng vẫn có PDF + ZPL + `.ldm.json`. Việc sinh `.lab` chạy riêng bằng CLI (`AutoDesignLabel.exe`) trên máy có CS6. | Mất tính năng one-click `.lab` trên web. |
| **B. Cài CS6 trên máy chủ + App Pool chạy bằng tài khoản người dùng thật** | Cài CodeSoft 6. Đặt *Identity* của App Pool = một domain/local user có license CS6, **Load User Profile = True**. Đăng nhập tương tác vào máy chủ bằng chính user đó một lần để CS6 tạo profile/kích hoạt license. Cân nhắc `hostingModel="outofprocess"`. | Phức tạp; COM vẫn có thể treo lẻ tẻ (code đã có `SemaphoreSlim` serialize + timeout để giảm rủi ro). |
| **C. Tách dịch vụ sinh `.lab`** | Đưa `LabFileGenerator` ra một Windows Service / scheduled task chạy trên máy có CS6, web chỉ đẩy yêu cầu. | Cần thêm phát triển. |

Dù chọn phương án nào: lỗi ở bước `.lab` **không làm hỏng** PDF/ZPL — trong CLI nó chỉ là cảnh
báo và không đổi exit code; trên web nó hiện thông báo lỗi ở nút Export và không chặn các nút khác.

Biến môi trường liên quan (đặt ở `<environmentVariables>` trong `web.config` nếu cần):

- `ADL_SKIP_LAB=1` — bỏ hẳn bước `.lab` (chỉ có tác dụng ở CLI `Program.cs`; nút web gọi
  `LabFileGenerator` trực tiếp nên không đọc biến này).
- `ADL_OCR_DEBUG=1` — dump barcode/OCR đã giải mã ra log (debug nhánh content-only).

---

## 10. Xử lý sự cố nhanh

| Triệu chứng | Nguyên nhân thường gặp |
|---|---|
| HTTP 500.19, lỗi `0x8007000d` | `web.config` sai định dạng, hoặc thiếu module ANCM → cài lại Hosting Bundle, `iisreset`. |
| HTTP 500.30 / 500.31 | App crash khi start. Bật `stdoutLogEnabled`, đọc `logs\stdout_*.log`. Hay gặp: thiếu `Microsoft.AspNetCore.App 8.0.x`. |
| HTTP 500.35 | Đang chạy `inprocess` mà có nhiều app trong cùng App Pool → mỗi app ASP.NET Core cần App Pool riêng. |
| Trang tải nhưng thao tác không phản hồi, console báo lỗi WebSocket | Chưa bật **WebSocket Protocol** trong IIS. |
| Upload PMD lớn bị ngắt | PMD > 50 MB → tăng `MaximumReceiveMessageSize` trong `Program.cs`, publish lại. |
| Render PDF sai font / thiếu chữ | Font JioType nhúng trong assembly nên hiếm khi lỗi; nếu có, kiểm tra `AutoDesignLabel.dll` trong publish có đúng bản mới không. |
| OCR không đọc được chữ (nhánh content-only) | Thiếu gói ngôn ngữ OCR trên máy chủ. Barcode vẫn giải mã bình thường — đây là suy giảm có kiểm soát. |
| `.lab` luôn lỗi | Xem mục 9 — mặc định coi như không khả dụng trên IIS trừ khi đã cài CS6 + cấu hình identity. |
| App tự tắt sau vài phút, lần truy cập sau chậm | Đặt App Pool *Idle Time-out* = 0 và *Start Mode* = AlwaysRunning. |

Log cần xem khi có sự cố:

- `C:\inetpub\AutoDesignLabel\logs\stdout_*.log` (khi bật `stdoutLogEnabled`).
- **Event Viewer → Windows Logs → Application**, source `IIS AspNetCore Module V2` và `.NET Runtime`.
- IIS access log: `C:\inetpub\logs\LogFiles\`.

---

## 11. Phụ lục — chạy trực tiếp không qua IIS (khi cần test nhanh trên máy chủ)

```powershell
cd C:\inetpub\AutoDesignLabel
$env:ASPNETCORE_URLS = "http://0.0.0.0:8081"
dotnet .\AutoDesignLabel.Web.dll
```

Mở `http://<máy-chủ>:8081/`. Cách này dùng để phân biệt lỗi do IIS/ANCM hay do bản thân app.
Nhớ mở port trên Windows Firewall nếu truy cập từ máy khác.
