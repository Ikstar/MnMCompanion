using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MnMCompanion.Gear;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace MnMCompanion;

/// <summary>
/// Windows built-in WinRT OCR adapter (<see cref="OcrEngine"/>). Resolves the engine lazily and degrades cleanly on
/// environments without Windows OCR language packs (such as Wine or Proton).
/// </summary>
internal sealed class WindowsGearOcr : IGearOcr
{
    private readonly object _gate = new();
    private bool _probed;
    private OcrEngine? _engine;
    private string? _unavailableReason;

    public bool IsAvailable
    {
        get
        {
            EnsureEngine();
            return _engine is not null;
        }
    }

    public string? UnavailableReason
    {
        get
        {
            EnsureEngine();
            return _unavailableReason;
        }
    }

    public async Task<string> ReadTextAsync(GrayImage image, CancellationToken cancel = default)
    {
        var engine = EnsureEngine();
        if (engine is null || image.Width <= 0 || image.Height <= 0) return "";

        int count = image.Width * image.Height;
        var bgra = new byte[count * 4];
        for (int i = 0, j = 0; i < count; i++, j += 4)
        {
            byte g = image.Pixels[i];
            bgra[j] = g;
            bgra[j + 1] = g;
            bgra[j + 2] = g;
            bgra[j + 3] = 255;
        }

        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
            bgra.AsBuffer(),
            BitmapPixelFormat.Bgra8,
            image.Width,
            image.Height,
            BitmapAlphaMode.Ignore);

        cancel.ThrowIfCancellationRequested();
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancel).ConfigureAwait(false);
        if (result.Lines is null || result.Lines.Count == 0) return "";

        var ordered = result.Lines
            .OrderBy(l => l.Words.Count > 0 ? l.Words.Min(w => w.BoundingRect.Y) : 0.0)
            .Select(l => l.Text?.Trim() ?? "")
            .Where(t => t.Length > 0);

        return string.Join('\n', ordered);
    }

    private OcrEngine? EnsureEngine()
    {
        lock (_gate)
        {
            if (_probed) return _engine;
            _probed = true;
            try
            {
                _engine = TryCreate("en-US")
                    ?? TryCreate("en")
                    ?? OcrEngine.TryCreateFromUserProfileLanguages();
                if (_engine is null)
                    _unavailableReason = "Windows OCR has no English or user-profile language pack installed.";
            }
            catch (Exception ex) when (ex is COMException or PlatformNotSupportedException or TypeLoadException or NotSupportedException)
            {
                _engine = null;
                _unavailableReason = $"Windows OCR is unavailable here ({ex.GetType().Name}).";
            }
            return _engine;
        }
    }

    private static OcrEngine? TryCreate(string tag)
    {
        try
        {
            var lang = new Language(tag);
            return OcrEngine.IsLanguageSupported(lang) ? OcrEngine.TryCreateFromLanguage(lang) : null;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or PlatformNotSupportedException or TypeLoadException)
        {
            return null;
        }
    }
}

/// <summary>Converts WPF clipboard/file bitmaps into plain <see cref="RgbImage"/> buffers on the UI thread.</summary>
internal static class ScreenshotLoader
{
    public static RgbImage FromBitmapSource(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Bgr24
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgr24, null, 0);

        int w = converted.PixelWidth;
        int h = converted.PixelHeight;
        int stride = ((w * 3 + 3) / 4) * 4;
        var bgr = new byte[stride * h];
        converted.CopyPixels(bgr, stride, 0);

        var rgb = new byte[w * h * 3];
        for (int y = 0; y < h; y++)
        {
            int srcRow = y * stride;
            int dstRow = y * w * 3;
            for (int x = 0; x < w; x++)
            {
                int si = srcRow + x * 3;
                int di = dstRow + x * 3;
                rgb[di] = bgr[si + 2];
                rgb[di + 1] = bgr[si + 1];
                rgb[di + 2] = bgr[si];
            }
        }
        return new RgbImage(w, h, rgb);
    }

    public static RgbImage FromFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("The image file has no frames.");
        return FromBitmapSource(decoder.Frames[0]);
    }
}
