using System.Collections.Concurrent;

namespace AutoDesignLabel.Web;

/// <summary>Cau ban ngan han giua Index.razor (sinh file .lab/.zpl server-side, ghi ra
/// Path.GetTempFileName) va endpoint /download-file/{token} - token GUID chi dung 1 lan, tu xoa
/// khoi bang ngay khi da phuc vu request tai xuong.</summary>
public static class FileDownloads
{
    public sealed record Entry(string Path, string FileName);

    public static readonly ConcurrentDictionary<string, Entry> Pending = new();
}
