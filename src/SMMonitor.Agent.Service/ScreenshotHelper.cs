using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;

namespace SMMonitor.Agent.Service;

public sealed class ScreenshotCaptureResult
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
    public string ContentType { get; init; } = "image/jpeg";
    public string ImageBase64 { get; init; } = "";
    public int Width { get; init; }
    public int Height { get; init; }

    public static ScreenshotCaptureResult NotEnabled() => new()
    {
        Ok = false,
        Error = "auto capture disabled"
    };

    public object ToData()
    {
        if (!Ok)
        {
            return new
            {
                ok = false,
                error = Error ?? "capture failed"
            };
        }

        return new
        {
            ok = true,
            imageBase64 = ImageBase64,
            contentType = ContentType,
            width = Width,
            height = Height
        };
    }
}

public static class ScreenshotHelper
{
    [SupportedOSPlatform("windows")]
    public static ScreenshotCaptureResult TryCapturePrimaryScreen(string imageFormat, int jpegQuality)
    {
        try
        {
            if (OperatingSystem.IsWindows() == false)
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "screenshot only supported on windows" };
            }

            var bounds = System.Windows.Forms.Screen.PrimaryScreen?.Bounds;
            if (bounds == null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "screen is not available in current session" };
            }

            using var bitmap = new Bitmap(bounds.Value.Width, bounds.Value.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.CopyFromScreen(bounds.Value.Left, bounds.Value.Top, 0, 0, bitmap.Size);
            }

            using var ms = new MemoryStream();
            var fmt = NormalizeFormat(imageFormat, out var contentType);

            if (fmt.Guid == ImageFormat.Jpeg.Guid)
            {
                var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => x.FormatID == ImageFormat.Jpeg.Guid);
                if (encoder != null)
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 30, 100));
                    bitmap.Save(ms, encoder, ep);
                }
                else
                {
                    bitmap.Save(ms, fmt);
                }
            }
            else
            {
                bitmap.Save(ms, fmt);
            }

            return new ScreenshotCaptureResult
            {
                Ok = true,
                ContentType = contentType,
                ImageBase64 = Convert.ToBase64String(ms.ToArray()),
                Width = bitmap.Width,
                Height = bitmap.Height,
            };
        }
        catch (Exception ex)
        {
            return new ScreenshotCaptureResult { Ok = false, Error = ex.Message };
        }
    }

    private static ImageFormat NormalizeFormat(string input, out string contentType)
    {
        var fmt = (input ?? "jpeg").Trim().ToLowerInvariant();
        return fmt switch
        {
            "png" => Set(ImageFormat.Png, "image/png", out contentType),
            "bmp" => Set(ImageFormat.Bmp, "image/bmp", out contentType),
            "webp" => Set(ImageFormat.Jpeg, "image/jpeg", out contentType),
            _ => Set(ImageFormat.Jpeg, "image/jpeg", out contentType),
        };

        static ImageFormat Set(ImageFormat format, string ct, out string contentTypeOut)
        {
            contentTypeOut = ct;
            return format;
        }
    }
}
