using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor(o => o.DetailedErrors = builder.Environment.IsDevelopment())
    // Mac dinh SignalR gioi han message ~32KB - PMD.pdf co the vai MB nen phai nang gioi han.
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 50 * 1024 * 1024);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// Cong cu noi bo trong LAN, khong bat HTTPS redirect de tranh moi nguoi dung phai xu ly canh bao
// chung chi tu ky khi truy cap qua ten may/IP noi bo.

app.UseStaticFiles();

app.UseRouting();

// Endpoint tai file .lab/.zpl da sinh (LabFileGenerator/ZplEmitter ghi ra file tam server-side,
// Index.razor gan token GUID ngan han vao AutoDesignLabel.Web.FileDownloads.Pending roi dieu huong
// trinh duyet den day de kich hoat download - mo hinh chuan cho Blazor Server, khong can JS interop
// blob).
app.MapGet("/download-file/{token}", (string token) =>
{
    if (!AutoDesignLabel.Web.FileDownloads.Pending.TryRemove(token, out var entry))
        return Results.NotFound();
    if (!File.Exists(entry.Path))
        return Results.NotFound();
    var bytes = File.ReadAllBytes(entry.Path);
    return Results.File(bytes, "application/octet-stream", entry.FileName);
});

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

app.Run();
