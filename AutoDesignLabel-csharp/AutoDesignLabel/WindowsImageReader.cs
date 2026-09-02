using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using ZXing;
using ZXing.Common;

namespace AutoDesignLabel;

/// <summary>
/// Doc anh nhan raster (PMD content-only) bang cong cu co san trong Windows 10 - hoan toan offline:
///   - Barcodes(): decode PNG/JPEG -> pixel (BitmapDecoder) -> ZXing.Net (managed) giai ma
///     Code128 / UPC-A / EAN-13 / QR. Gia tri co checksum nen CHINH XAC.
///   - Ocr(): Windows.Media.Ocr (can goi ngon ngu OCR; neu khong co -> tra rong, barcode van chay).
///
/// BitmapDecoder luon co tren Win10 nen TryCreate gan nhu luon thanh cong; OCR engine la tuy chon.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsImageReader : IPmdImageReader
{
    private readonly OcrEngine? _ocr;

    private WindowsImageReader(OcrEngine? ocr) => _ocr = ocr;

    public bool HasOcr => _ocr is not null;

    public static WindowsImageReader? TryCreate()
    {
        try
        {
            // Xac nhan BitmapDecoder dung duoc (giai ma anh) - bat buoc cho ca barcode lan OCR.
            _ = typeof(BitmapDecoder);
            OcrEngine? engine = null;
            try
            {
                engine = OcrEngine.TryCreateFromLanguage(new Language("en"))
                         ?? OcrEngine.TryCreateFromUserProfileLanguages();
            }
            catch { /* khong co goi ngon ngu OCR - van dung barcode */ }
            return new WindowsImageReader(engine);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Barcode
    // ------------------------------------------------------------------

    private static readonly BarcodeReaderGeneric BarcodeReader = new()
    {
        AutoRotate = true,
        Options = new DecodingOptions
        {
            TryHarder = true,
            PureBarcode = false,
            PossibleFormats = new[]
            {
                BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
                BarcodeFormat.UPC_A, BarcodeFormat.EAN_13, BarcodeFormat.EAN_8,
                BarcodeFormat.QR_CODE,
            },
        },
    };

    public IReadOnlyList<DecodedBarcode> Barcodes(byte[] imageBytes)
    {
        try
        {
            var (bgra, w, h) = DecodePixelsAsync(imageBytes, maxDim: 4000).GetAwaiter().GetResult();
            var source = new RGBLuminanceSource(bgra, w, h, RGBLuminanceSource.BitmapFormat.BGRA32);

            var results = BarcodeReader.DecodeMultiple(source);
            if (results is null || results.Length == 0)
            {
                var one = BarcodeReader.Decode(source);
                if (one is null) return Array.Empty<DecodedBarcode>();
                results = new[] { one };
            }

            return results
                .Where(r => !string.IsNullOrWhiteSpace(r.Text))
                .Select(r => new DecodedBarcode(r.BarcodeFormat.ToString(), r.Text.Trim()))
                .DistinctBy(b => b.Format + "" + b.Value)
                .ToList();
        }
        catch
        {
            return Array.Empty<DecodedBarcode>();
        }
    }

    // ------------------------------------------------------------------
    // OCR
    // ------------------------------------------------------------------

    public IReadOnlyList<OcrLine> Ocr(byte[] imageBytes)
    {
        if (_ocr is null) return Array.Empty<OcrLine>();
        try { return OcrAsync(imageBytes).GetAwaiter().GetResult(); }
        catch { return Array.Empty<OcrLine>(); }
    }

    private async Task<IReadOnlyList<OcrLine>> OcrAsync(byte[] imageBytes)
    {
        using var bitmap = await LoadBitmapAsync(imageBytes, OcrEngine.MaxImageDimension);
        var result = await _ocr!.RecognizeAsync(bitmap);
        return result.Lines
            .Select(l => l.Text?.Trim())
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => new OcrLine(t!, 0.7f))
            .ToList();
    }

    // ------------------------------------------------------------------
    // Giai ma anh -> pixel
    // ------------------------------------------------------------------

    private static async Task<(byte[] Bgra, int Width, int Height)> DecodePixelsAsync(byte[] bytes, uint maxDim)
    {
        using var bmp = await LoadBitmapAsync(bytes, maxDim);
        var buffer = new byte[4 * bmp.PixelWidth * bmp.PixelHeight];
        bmp.CopyToBuffer(buffer.AsBuffer());
        return (buffer, bmp.PixelWidth, bmp.PixelHeight);
    }

    private static async Task<SoftwareBitmap> LoadBitmapAsync(byte[] bytes, uint maxDim)
    {
        using var ras = new InMemoryRandomAccessStream();
        await ras.WriteAsync(bytes.AsBuffer());
        ras.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(ras);

        var transform = new BitmapTransform();
        uint pw = decoder.PixelWidth, ph = decoder.PixelHeight;
        if (pw > maxDim || ph > maxDim)
        {
            var scale = Math.Min((double)maxDim / pw, (double)maxDim / ph);
            transform.ScaledWidth = (uint)(pw * scale);
            transform.ScaledHeight = (uint)(ph * scale);
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }
}
