using System.Net.Sockets;
using System.Text;

namespace AutoDesignLabel;

/// <summary>Gui ZPL thang qua raw socket 9100 - khong can driver, khong license.</summary>
public sealed class PrinterClient
{
    private readonly string _host;
    private readonly int _port;
    public PrinterClient(string host, int port = 9100) { _host = host; _port = port; }

    public async Task SendAsync(string zpl, CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);
        var bytes = Encoding.UTF8.GetBytes(zpl);
        await client.GetStream().WriteAsync(bytes, ct);
        await client.GetStream().FlushAsync(ct);
    }

    /// <summary>Doc trang thai may in (~HS) de biet het giay / ket ribbon truoc khi in lo lon.</summary>
    public async Task<string> QueryStatusAsync(CancellationToken ct = default)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(_host, _port, ct);
        var s = client.GetStream();
        await s.WriteAsync(Encoding.ASCII.GetBytes("~HS"), ct);
        var buf = new byte[512];
        var n = await s.ReadAsync(buf, ct);
        return Encoding.ASCII.GetString(buf, 0, n);
    }
}
