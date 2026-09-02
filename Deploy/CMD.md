# CMD.md — Các lệnh deploy Auto Design Label lên IIS

Danh sách lệnh theo thứ tự thực hiện, kèm giải thích. Bối cảnh kiến trúc xem `../README.md`.

- **Máy DEV** = máy có mã nguồn + .NET 8 SDK, nơi chạy `dotnet publish`.
- **Máy SERVER** = máy chạy IIS.
- Trên máy SERVER, mọi lệnh chạy trong **PowerShell mở bằng "Run as administrator"**.
- Trên máy này (`BILLY-SU`) DEV và SERVER là **cùng một máy**.

---

## Tóm tắt tổng quát

Deploy gồm 2 nhóm việc: **cài đặt một lần** (mục 2–5, thêm mục 10 nếu cần Export `.lab`) và
**lặp lại mỗi lần ra bản mới** (mục 1, 7).

| Mục | Khi nào chạy | Lệnh chính | Mục đích |
|---|---|---|---|
| **0** | Mỗi phiên PowerShell | Đặt biến `$Src` `$Publish` `$SiteDir` `$Pool` `$AppUrl` | Tránh gõ sai đường dẫn ở các lệnh sau |
| **1** | Mỗi lần deploy | `dotnet publish ... -c Release -o $Publish` | Gom app + DLL + `web.config` vào 1 thư mục để copy đi. Kèm cách chạy thử không qua IIS |
| **2** | Một lần / máy SERVER | Tải + cài **Hosting Bundle 8.x** (`/install /quiet /norestart`) → `iisreset` | Cài .NET Runtime + **module ANCM** cho IIS. Thiếu bước này = lỗi **500.19 / `0x8007000d`** |
| **3** | Một lần / máy SERVER | `Enable-WindowsOptionalFeature ... IIS-WebSockets` | Bật IIS + **WebSocket Protocol** (Blazor Server bắt buộc) |
| **4** | Một lần / máy SERVER | `New-WebAppPool` (No Managed Code, `loadUserProfile=$true`, `idleTimeout=0`) → `New-Website -Port 8080` | Tạo App Pool + Site. Kèm biến thể **app con `/autodesign`** và lệnh đổi pool |
| **5** | Một lần / máy SERVER | `icacls $SiteDir ...` + tạo & cấp quyền `logs\` | Phân quyền: thư mục site read-only, `logs\` cho ghi |
| **6** | Sau mỗi deploy | `Invoke-WebRequest` trang chủ / `blazor.server.js` / **`_blazor/negotiate`** | Kiểm chứng chuỗi IIS → ANCM → Kestrel → SignalR đã thông |
| **7** | Mỗi lần deploy | `Stop-WebAppPool` → `Copy-Item` đè → `Start-WebAppPool` (hoặc `app_offline.htm`) | Cập nhật bản mới lên SERVER |
| **8** | Khi có sự cố | Đọc `stdout` log, query Event Log, xem cấu hình pool, `Restart-WebAppPool` | Chẩn đoán + bảng mã lỗi 500.19 / 500.30 / 500.35 / 502.5 |
| **9** | Tùy chọn | Sửa `Program.cs` + `icacls` thư mục key | Cố định Data Protection key (khỏi phải F5 lại khi pool recycle). Có thể bỏ qua với công cụ LAN |
| **10** | Một lần / máy SERVER — chỉ khi cần **Export `.lab`** | IIS Manager → App Pool → **Identity = Custom account** (admin có CS6) | CS6 dùng COM automation, `ApplicationPoolIdentity` không có quyền → lỗi `0x80070005 Access is denied`. Đổi identity sang tài khoản Windows thật đã cài + kích hoạt license CS6 |

**Luồng deploy lần đầu:** `0 → 1 → 2 → 3 → 4 → 5 → 6` (mục 4.1 đã copy bản build sẵn, không cần mục 7)
**Luồng cập nhật về sau:** `0 → 1 → 7 → 6`
**Nếu dùng Export `.lab`:** làm thêm **mục 10** một lần sau khi deploy xong.

---

## 0. Biến đường dẫn (dán một lần vào đầu phiên PowerShell trên SERVER)

```powershell
$Src     = "D:\2026\dev\Auto-Design-Label\AutoDesignLabel-csharp"   # thư mục mã nguồn C#
$Publish = "$Src\publish\AutoDesignLabel.Web"                       # đích của dotnet publish
$SiteDir = "C:\inetpub\AutoDesignLabel"                             # nơi IIS chạy app (bản đã copy)
$Pool    = "AutoDesign"                                             # tên Application Pool
$AppUrl  = "http://localhost:8080/"                                 # URL kiểm tra
```

**Giải thích:** đặt biến để các lệnh bên dưới dùng lại, tránh gõ sai đường dẫn.
Nếu deploy chạy thẳng từ thư mục publish trong repo (như lần test đầu), đặt `$SiteDir = $Publish`.

---

## 1. Build trên máy DEV

```powershell
cd $Src

# (khuyến nghị) xóa output cũ để không lẫn file thừa
if (Test-Path $Publish) { Remove-Item $Publish -Recurse -Force }

dotnet publish AutoDesignLabel.Web\AutoDesignLabel.Web.csproj -c Release -o $Publish
```

**Giải thích từng phần:**

| Thành phần | Ý nghĩa |
|---|---|
| `dotnet publish` | Biên dịch **và gom** app + toàn bộ DLL phụ thuộc + `wwwroot` + `web.config` vào một thư mục hoàn chỉnh để copy đi deploy. Khác `dotnet build` (chỉ ra `.dll` chạy dev). |
| `AutoDesignLabel.Web\AutoDesignLabel.Web.csproj` | Chỉ publish project web. .NET **tự kéo theo** project lõi `AutoDesignLabel` vì được tham chiếu — không cần build lõi riêng. **Không** publish cả `.sln` (project rỗng `AutoDesignLabel.Wpf` sẽ làm lỗi `MSB4057`). |
| `-c Release` | Cấu hình Release (tối ưu, bỏ mã debug). Mặc định là `Debug`. |
| `-o $Publish` | Thư mục đích. Không có `-o` thì output nằm sâu trong `bin\Release\...\publish\`. |

Kết quả (~34 MB) gồm `AutoDesignLabel.Web.dll` (app), `AutoDesignLabel.dll` (lõi, có font JioType
nhúng sẵn bên trong nên **không** có thư mục `Font\`), `web.config` (IIS đọc file này), `wwwroot\`.
Các cảnh báo `MSB4011 / NETSDK1086 / CS0618 / CS1668` khi publish là **vô hại** — miễn dòng cuối in
`AutoDesignLabel.Web -> ...\publish\AutoDesignLabel.Web\`.

### Kiểm chứng nhanh bản build (không qua IIS)

```powershell
cd $Publish
$env:ASPNETCORE_URLS = "http://127.0.0.1:8199"
dotnet .\AutoDesignLabel.Web.dll
# mở http://127.0.0.1:8199/ -> thấy trang review -> Ctrl+C để dừng
```

**Giải thích:** chạy trực tiếp bằng .NET runtime, bỏ qua IIS. Nếu bước này chạy được mà qua IIS lỗi
thì vấn đề nằm ở IIS/ANCM, không phải ở app.

---

## 2. Cài .NET runtime cho IIS trên máy SERVER (làm MỘT LẦN)

```powershell
# Tải ASP.NET Core Hosting Bundle 8.0.x
Invoke-WebRequest "https://aka.ms/dotnet/8.0/dotnet-hosting-win.exe" `
    -OutFile "$env:USERPROFILE\Downloads\dotnet-hosting-8.0-win.exe"

# Cài (im lặng, không tự khởi động lại máy)
& "$env:USERPROFILE\Downloads\dotnet-hosting-8.0-win.exe" /install /quiet /norestart

# Nạp lại IIS để nhận module mới
iisreset
```

**Giải thích:**

- **Hosting Bundle** gộp 3 thứ: (a) .NET Runtime 8, (b) ASP.NET Core Runtime 8, (c) module
  **`AspNetCoreModuleV2` (ANCM)** cắm vào IIS để IIS biết cách khởi động app .NET. **Không cần cài SDK**
  trên server.
- Chỉ có runtime `Microsoft.AspNetCore.App` (ví dụ do Visual Studio cài) là **KHÔNG đủ** — thiếu
  module ANCM thì IIS đọc `web.config` không hiểu `<add modules="AspNetCoreModuleV2">` và báo
  **HTTP 500.19 / `0x8007000d`** với Config Source trống (`-1: / 0:`).
- `/quiet /norestart`: cài không hiện UI, không reboot. Bỏ `/quiet` nếu muốn xem giao diện cài.
- `iisreset` phải chạy **sau** khi cài, nếu không IIS chưa nạp module.

> **Bẫy thứ tự:** nếu Hosting Bundle được cài **trước** khi có IIS → module không đăng ký. Khắc phục:
> `& "...\dotnet-hosting-8.0-win.exe" /repair /quiet` rồi `iisreset`.

### Kiểm tra runtime + module đã sẵn sàng

```powershell
# Phải thấy dòng "Microsoft.AspNetCore.App 8.0.x"
dotnet --list-runtimes | Select-String "AspNetCore.App 8"

# File module phải TỒN TẠI ở đường dẫn này (KHÔNG phải trong C:\Windows\System32\inetsrv)
Test-Path "C:\Program Files\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"

# Module phải được đăng ký trong cấu hình IIS
& "$env:windir\system32\inetsrv\appcmd.exe" list config `
    -section:system.webServer/globalModules | Select-String AspNetCore
```

**Giải thích:** `aspnetcorev2.dll` của Hosting Bundle đời mới nằm ở
`C:\Program Files\IIS\Asp.Net Core Module\V2\` — **không** ở `C:\Windows\System32\inetsrv\` (chỗ đó là
ANCM V1 đời cũ). Tìm nhầm chỗ sẽ tưởng là chưa cài.

---

## 3. Bật tính năng IIS cần thiết trên SERVER (làm MỘT LẦN)

**Windows 10 / 11:**

```powershell
Enable-WindowsOptionalFeature -Online -All -FeatureName `
  IIS-WebServerRole, IIS-WebServer, IIS-WebSockets, IIS-ManagementConsole
```

**Windows Server:**

```powershell
Install-WindowsFeature -Name Web-Server, Web-Asp-Net45, Web-WebSockets -IncludeManagementTools
```

Kiểm tra WebSocket (bắt buộc cho Blazor Server):

```powershell
Get-WindowsOptionalFeature -Online -FeatureName IIS-WebSockets | Select-Object State
# hoặc trên Windows Server:
Get-WindowsFeature Web-WebSockets
```

**Giải thích:** app là **Blazor Server** — trình duyệt giữ một kết nối SignalR sống liên tục
(ưu tiên WebSocket) để máy chủ đẩy giao diện. Thiếu **WebSocket Protocol** thì trang tải được nhưng
bấm nút không phản hồi (SignalR rơi về long-polling và hay đứt).

---

## 4. Tạo Application Pool + Site trên SERVER (làm MỘT LẦN)

```powershell
Import-Module WebAdministration

# 4.1. Copy bản build lên thư mục chạy
New-Item $SiteDir -ItemType Directory -Force | Out-Null
Copy-Item "$Publish\*" $SiteDir -Recurse -Force

# 4.2. Tạo App Pool
New-WebAppPool -Name $Pool
Set-ItemProperty "IIS:\AppPools\$Pool" -Name managedRuntimeVersion -Value ""          # = "No Managed Code"
Set-ItemProperty "IIS:\AppPools\$Pool" -Name startMode             -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.idleTimeout     -Value "00:00:00"
Set-ItemProperty "IIS:\AppPools\$Pool" -Name processModel.loadUserProfile -Value $true

# 4.3. Tạo Site riêng, cổng 8080
New-Website -Name "AutoDesignLabel" -PhysicalPath $SiteDir -ApplicationPool $Pool `
            -Port 8080 -Force

# 4.4. Khởi động
Start-WebAppPool -Name $Pool
Start-Website    -Name "AutoDesignLabel"
```

**Giải thích từng cài đặt:**

| Lệnh | Vì sao |
|---|---|
| `managedRuntimeVersion = ""` | = **"No Managed Code"**. App .NET Core tự chứa runtime của nó, không dùng CLR do IIS quản. Bắt buộc cho mọi app ASP.NET Core. |
| `startMode = AlwaysRunning` | Giữ app khởi động sẵn, người dùng đầu tiên không phải chờ nạp. |
| `idleTimeout = 00:00:00` | Không tắt app khi rảnh. Quan trọng vì phiên Blazor Server là **stateful** — app tắt thì người đang thao tác mất phiên. |
| `loadUserProfile = $true` | **Bắt buộc.** `FontInstaller` ghi font vào registry `HKCU`, và app ghi file tạm `.lab/.zpl/.ldm.json` vào `%TEMP%` của identity. Không load profile → hai việc này hỏng. |
| `New-Website ... -Port 8080` | Tạo **site riêng**, không phải app con dưới "Default Web Site". App không cấu hình `UsePathBase`; tuy dạng app con vẫn chạy được với host in-process, site riêng vẫn là cách sạch nhất và khớp `README.md`. |

> **Identity:** để `ApplicationPoolIdentity` (mặc định) là đủ cho PDF/ZPL. Nếu cần **Export `.lab`**
> thì phải đổi Identity sang tài khoản Windows thật — xem **mục 10**.

### Biến thể: deploy dạng ứng dụng con `/autodesign` dưới "Default Web Site"

```powershell
Import-Module WebAdministration
New-WebApplication -Site "Default Web Site" -Name "autodesign" `
                   -PhysicalPath $SiteDir -ApplicationPool $Pool
```

**Giải thích:** truy cập qua `http://localhost/autodesign`. Với host **in-process** của IIS, ANCM tự
truyền "path base" nên `<base href="/autodesign/">` và SignalR hub vẫn đúng — không cần sửa code.
**Lưu ý:** phải trỏ app con này vào pool `$Pool` (No Managed Code), **đừng** để nó dùng chung pool
`.NET v4.5` với các app ASP.NET Framework khác — dễ gây `HTTP 500.35` và làm rung các app kia.

Nếu lỡ tạo nhầm pool, đổi lại:

```powershell
Set-ItemProperty "IIS:\Sites\Default Web Site\autodesign" -Name applicationPool -Value $Pool
Restart-WebAppPool -Name $Pool
```

---

## 5. Phân quyền thư mục trên SERVER (làm MỘT LẦN)

```powershell
$acct = "IIS AppPool\$Pool"        # tài khoản ảo của App Pool

# Thư mục site: chỉ cần đọc + chạy
icacls $SiteDir /grant "${acct}:(OI)(CI)(RX)" /T

# Thư mục log của ANCM: cần quyền ghi
New-Item "$SiteDir\logs" -ItemType Directory -Force | Out-Null
icacls "$SiteDir\logs" /grant "${acct}:(OI)(CI)(M)"
```

**Giải thích:**

- `IIS AppPool\<tên pool>` là tài khoản ảo IIS tự tạo cho mỗi Application Pool
  (`ApplicationPoolIdentity`).
- `(OI)(CI)` = áp quyền cho file và thư mục con; `(RX)` = đọc + thực thi; `(M)` = sửa/ghi; `/T` = đệ quy.
- File tạm khi export `.lab/.zpl` ghi vào `%TEMP%` của identity (đã có nhờ `loadUserProfile`), **không**
  ghi vào thư mục site — nên thư mục site để read-only là đủ và an toàn hơn.

---

## 6. Kiểm tra sau khi deploy

```powershell
# 6.1. Trang chủ phải trả HTTP 200 và đúng tiêu đề
$r = Invoke-WebRequest $AppUrl -UseBasicParsing -TimeoutSec 30
"HTTP $($r.StatusCode)"
if ($r.Content -match '<title>(.*?)</title>') { "title: $($matches[1])" }   # -> Auto Design Label

# 6.2. Tài nguyên Blazor phải tải được
Invoke-WebRequest ($AppUrl + "_framework/blazor.server.js") -UseBasicParsing |
  ForEach-Object { "blazor.server.js: HTTP $($_.StatusCode)" }

# 6.3. Kết nối SignalR (bài test thật của Blazor Server)
$neg = Invoke-WebRequest ($AppUrl + "_blazor/negotiate?negotiateVersion=1") `
       -Method POST -UseBasicParsing -TimeoutSec 20
($neg.Content | ConvertFrom-Json) |
  Select-Object connectionId, @{n='transports';e={($_.availableTransports.transport) -join ','}}
# transports phải chứa "WebSockets"

# 6.4. Xác nhận app chạy trong w3wp của đúng pool
Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" | ForEach-Object {
  "PID $($_.ProcessId)  pool=" + ($_.CommandLine -replace '.*-ap "([^"]+)".*','$1')
}
```

**Giải thích:** bước 6.3 quan trọng nhất — `negotiate` trả `200` và có `WebSockets` trong danh sách
transport nghĩa là toàn bộ chuỗi IIS → ANCM → Kestrel → SignalR đã thông. Nếu chỉ có
`ServerSentEvents, LongPolling` → thiếu WebSocket Protocol (quay lại mục 3).

---

## 7. Cập nhật phiên bản mới (mỗi lần deploy lại)

```powershell
# --- máy DEV ---
cd $Src
dotnet publish AutoDesignLabel.Web\AutoDesignLabel.Web.csproj -c Release -o $Publish

# --- máy SERVER ---
Import-Module WebAdministration
Stop-WebAppPool -Name $Pool                       # nhả khóa file .dll
Copy-Item "$Publish\*" $SiteDir -Recurse -Force   # copy đè
Start-WebAppPool -Name $Pool

# kiểm tra lại
(Invoke-WebRequest $AppUrl -UseBasicParsing -TimeoutSec 30).StatusCode
```

**Cách "không cần stop"** (giảm gián đoạn):

```powershell
# tạo app_offline.htm -> ANCM tự tắt app, nhả khóa
New-Item "$SiteDir\app_offline.htm" -ItemType File -Force | Out-Null
Copy-Item "$Publish\*" $SiteDir -Recurse -Force -Exclude app_offline.htm
Remove-Item "$SiteDir\app_offline.htm"            # xóa -> app tự chạy lại
```

**Giải thích:**

- `Stop-WebAppPool` cần thiết vì khi app đang chạy, file `AutoDesignLabel*.dll` bị khóa, copy đè sẽ lỗi
  `Access denied`.
- Copy đè **ghi đè luôn `web.config`**. Nếu từng sửa tay `web.config` trên server (ví dụ bật log,
  thêm biến môi trường) thì phải áp lại sau khi copy.
- `app_offline.htm`: file rỗng đặt ở gốc site, ANCM thấy là dừng app ngay và trả trang đó cho mọi
  request. Xóa đi thì app khởi động lại. Không phải stop pool nên các app khác cùng pool (nếu có)
  không bị ảnh hưởng.

---

## 8. Lệnh chẩn đoán khi gặp sự cố

```powershell
# 8.1. Bật log stdout của ANCM (sửa web.config trên server)
#      trong thẻ <aspNetCore ...> đổi stdoutLogEnabled="false" -> "true"
#      rồi tái tạo lỗi, đọc:
Get-Content "$SiteDir\logs\stdout_*.log" -Tail 50

# 8.2. Lỗi ANCM / .NET trong Event Log 15 phút gần đây
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=(Get-Date).AddMinutes(-15)} -EA SilentlyContinue |
  Where-Object { $_.ProviderName -match 'IIS AspNetCore|\.NET Runtime|Application Error' } |
  Select-Object TimeCreated, LevelDisplayName, @{n='Msg';e={$_.Message.Split("`n")[0]}} |
  Format-Table -AutoSize -Wrap

# 8.3. Kiểm tra cấu hình app / pool hiện tại
Get-WebApplication -Site "Default Web Site" | Select-Object Path, ApplicationPool, PhysicalPath
Get-Item "IIS:\AppPools\$Pool" | Select-Object name, managedRuntimeVersion, state, startMode
(Get-ItemProperty "IIS:\AppPools\$Pool" -Name processModel).processModel |
  Select-Object identityType, loadUserProfile, idleTimeout

# 8.4. Recycle nhanh khi app "treo" hoặc sau khi đổi cấu hình pool
Restart-WebAppPool -Name $Pool
```

**Bảng mã lỗi thường gặp:**

| Mã | Nguyên nhân | Lệnh xử lý |
|---|---|---|
| `500.19` + `0x8007000d`, Config Source trống | Thiếu module ANCM | Mục 2 (cài Hosting Bundle) + `iisreset` |
| `500.30` / `500.31` | App crash khi khởi động | Mục 8.1 đọc `stdout`; hay do thiếu `AspNetCore.App 8.0.x` |
| `500.35` | Nhiều app ASP.NET Core in-process chung 1 pool | Cho app này 1 pool riêng (mục 4.2) |
| `502.5` | Sai phiên bản runtime / `hostingModel` | Kiểm tra `dotnet --list-runtimes`, `web.config` |
| Trang tải nhưng bấm nút đơ | Thiếu WebSocket Protocol | Mục 3 |
| `EphemeralXmlRepository` trong Event Log | Data Protection key lưu trong RAM, mất khi pool restart → người dùng phải F5 lại | Mục 9 (tùy chọn) |
| Export `.lab`: `Retrieving the COM class factory ... 0x80070005 (E_ACCESSDENIED)` | App Pool chạy bằng `ApplicationPoolIdentity` — không có quyền COM automation CS6 | Mục 10 |
| Recycle App Pool báo `0x800710D8` "object identifier does not represent a valid object" | Pool chưa có worker nào đang chạy để recycle — **không phải lỗi**. Dùng **Stop → Start** thay cho Recycle, hoặc mở app trên trình duyệt trước | — |

---

## 9. (Tùy chọn) Cố định Data Protection key

Triệu chứng: Event Log báo `Microsoft.AspNetCore.DataProtection ... EphemeralXmlRepository`. Nghĩa là
key mã hoá phiên Blazor / antiforgery **lưu trong RAM**, mất mỗi lần App Pool recycle → mọi người đang
mở phải tải lại trang (không mất dữ liệu, chỉ đứt phiên).

Cần **sửa code** `AutoDesignLabel.Web\Program.cs` (thêm vài dòng `AddDataProtection().PersistKeysToFileSystem(...)`),
publish lại, rồi cấp quyền ghi cho thư mục chứa key:

```powershell
$KeyDir = "C:\inetpub\AutoDesignLabel-keys"
New-Item $KeyDir -ItemType Directory -Force | Out-Null
icacls $KeyDir /grant "IIS AppPool\$Pool:(OI)(CI)(M)"
```

**Giải thích:** đặt thư mục key **ngoài** thư mục site để không bị copy đè khi cập nhật phiên bản.
Chỉ App Pool identity cần quyền sửa (`M`) thư mục này.

> Với công cụ nội bộ LAN, nếu chấp nhận "F5 lại khi hiếm khi pool recycle" thì có thể **bỏ qua mục 9**.

---

## 10. Cấu hình để Export `.lab` chạy được (CodeSoft 6) — làm bằng GUI

Chỉ cần khi muốn nút **Export .lab** hoạt động trên server. PDF/ZPL không cần bước này.

### Vì sao

`.lab` được sinh bằng cách điều khiển **CodeSoft 6 qua COM automation** (`lppa.exe`, COM server dạng
LocalServer32, **không** có khoá `AppID` nên dùng quyền DCOM mặc định của máy). App Pool mặc định chạy
bằng tài khoản ảo `ApplicationPoolIdentity` (chỉ thuộc nhóm `IIS_IUSRS`) → **không có quyền
Launch/Activation** → bấm Export .lab báo:

```
Retrieving the COM class factory for component with CLSID {3624B9C0-9E5D-11D3-A896-00C04F324E22}
failed ... 0x80070005 Access is denied. (E_ACCESSDENIED)
```

Cách khắc phục: cho App Pool chạy bằng **một tài khoản Windows thật** đã cài CS6 + kích hoạt license +
**đã đăng nhập interactive ít nhất một lần** (để CS6 tạo profile / config trong `%APPDATA%`).

### Bước 1 — Chuẩn bị tài khoản (làm một lần)

- Chọn một tài khoản local admin trên server (ví dụ tài khoản `SERVER\Administrator` bạn đang dùng để
  deploy).
- Đăng nhập RDP vào server **bằng chính tài khoản đó**, mở **CodeSoft 6** thủ công một lần: để nó khởi
  tạo, kích hoạt license, tắt hết hộp thoại first-run, rồi đóng lại.
- Nên đặt tài khoản này **Password never expires** — nếu mật khẩu hết hạn/đổi, App Pool sẽ chết cho tới
  khi cập nhật lại mật khẩu trong cấu hình pool.

### Bước 2 — Đổi Identity của App Pool (IIS Manager)

1. **Internet Information Services (IIS) Manager** → node **Application Pools**.
2. Chọn pool **AutoDesign** → khung **Actions** (bên phải) → **Advanced Settings…**
3. Nhóm **Process Model** → dòng **Identity** → bấm nút `...`.
4. Chọn **Custom account** → **Set…**
   - **User name:** `SERVER\Administrator` (đúng `TÊN-MÁY\tên-tài-khoản`; xem bằng lệnh `whoami`)
   - **Password** / **Confirm password:** gõ cẩn thận, đúng ký tự đặc biệt.
   - **OK** → **OK**.
5. Cùng bảng Advanced Settings: dòng **Load User Profile** → đổi thành **True**.
6. **OK** để đóng.

### Bước 3 — Khởi động lại pool bằng **Stop → Start** (KHÔNG dùng Recycle)

1. Vẫn ở **Application Pools**, chọn **AutoDesign**.
2. Actions → **Stop** → đợi Status = **Stopped**.
3. Actions → **Start**.
   - Status trở lại **Started** và giữ nguyên → OK.
   - Vừa Start xong lại **tự Stopped** → sai mật khẩu hoặc thiếu quyền → xem **Xử lý sự cố** bên dưới.

> Recycle lúc này có thể báo `0x800710D8` — vô hại (pool chưa có worker để recycle). Cứ dùng
> **Stop → Start**, hoặc mở app trên trình duyệt để nó tạo worker mới.

### Bước 4 — Xác nhận identity đang chạy

1. Mở app trên trình duyệt (trang phải hiện ra).
2. **Task Manager** → tab **Details** → bấm phải thanh tiêu đề cột → **Select columns** → tích
   **User name** và **Command line** → OK.
3. Tìm dòng `w3wp.exe` có **Command line** chứa `-ap "AutoDesign"` → cột **User name** phải là tài
   khoản bạn vừa đặt (ví dụ `Administrator`).
4. Vào web → bấm **Export .lab** → không còn lỗi COM.

### Xử lý sự cố (pool tự Stop sau khi Start)

1. **Sai mật khẩu:** đặt lại ở Advanced Settings → Identity → `...` → Custom account → Set…
2. **Thiếu quyền "Log on as a batch job":** `secpol.msc` → **Local Policies → User Rights Assignment**
   → **Log on as a batch job** → Add tài khoản. Đồng thời kiểm tra **Deny log on as a batch job**
   **không** chứa tài khoản đó / nhóm `Administrators`.
3. **Xem lý do thật:** `eventvwr.msc` → **Windows Logs → System** → nguồn **WAS** (Warning/Error gần
   nhất) — ghi rõ "password is invalid" hay "does not have the required user right".

### Nếu qua được Access Denied nhưng Export `.lab` **treo / timeout**

Đó là giới hạn **Session 0**: `lppa.exe` là app GUI chạy headless trong ngữ cảnh service, TEKLYNX hay
đứng. `LabFileGenerator` có timeout nên sẽ báo lỗi chứ không freeze hẳn. Lúc đó `.lab` không chạy ổn
định dưới IIS được — phải:

- tách việc sinh `.lab` ra một **worker chạy trong phiên đăng nhập** (Task Scheduler, "Run only when
  user is logged on"), web chỉ ghi yêu cầu; hoặc
- sinh `.lab` bằng **CLI `AutoDesignLabel.exe`** trên một máy trạm có CS6.

### Tương đương bằng lệnh (nếu không muốn dùng GUI)

```powershell
$a = "$env:windir\system32\inetsrv\appcmd.exe"
& $a set apppool "AutoDesign" /processModel.identityType:SpecificUser
& $a set apppool "AutoDesign" /processModel.userName:"SERVER\Administrator"
& $a set apppool "AutoDesign" /processModel.password:"<mat-khau>"
& $a set apppool "AutoDesign" /processModel.loadUserProfile:true
& $a stop  apppool "AutoDesign"
& $a start apppool "AutoDesign"

# kiểm tra identity của worker
Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" |
  Select-Object ProcessId, @{n='User';e={ $_.GetOwner().User }}, CommandLine
```

> `appcmd` luôn chạy được; PowerShell `Set-ItemProperty "IIS:\AppPools\..."` cần
> `Import-Module WebAdministration` trước (nếu không sẽ báo *"A drive with the name 'IIS' does not exist"*).
