using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SMMonitor.Common;

namespace SMMonitor.Agent.Manager;

public static class ManagerCommandServer
{
    public static async Task RunAsync(Action<string> log, CancellationToken token)
    {
        var pipeName = AgentSettings.FixedPipeName + "_manager_cmd";
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Message,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8, true, leaveOpen: true);
                await using var writer = new StreamWriter(server, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                while (!token.IsCancellationRequested && server.IsConnected)
                {
                    var line = await reader.ReadLineAsync(token);
                    if (line == null) break;
                    var result = HandleRequest(line);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result));
                    log(result.ok
                        ? $"[ManagerCmd] capture ok {result.width}x{result.height}"
                        : $"[ManagerCmd] capture failed: {result.error}");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // Client disconnected (normal path after single request/response).
                // Do not spam UI log with "Pipe is broken".
                await Task.Delay(80, token);
            }
            catch (Exception ex)
            {
                log($"[ManagerCmd] loop error: {ex.Message}");
                await Task.Delay(500, token);
            }
        }
    }

    private static (bool ok, string? error, string contentType, string imageBase64, int width, int height) HandleRequest(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var actionEl) ? (actionEl.GetString() ?? "") : "";
            if (!string.Equals(action, "capture_screen", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"unsupported action: {action}", "image/jpeg", "", 0, 0);
            }

            var imageFormat = root.TryGetProperty("imageFormat", out var fmt) ? (fmt.GetString() ?? "jpeg") : "jpeg";
            var quality = root.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv) ? Math.Clamp(qv, 30, 100) : 70;
            return CapturePrimaryScreen(imageFormat, quality);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, "image/jpeg", "", 0, 0);
        }
    }

    private static (bool ok, string? error, string contentType, string imageBase64, int width, int height) CapturePrimaryScreen(string imageFormat, int jpegQuality)
    {
        var bounds = Screen.PrimaryScreen?.Bounds;
        if (bounds == null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
        {
            return (false, "manager screen is not available", "image/jpeg", "", 0, 0);
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

        return (true, null, contentType, Convert.ToBase64String(ms.ToArray()), bitmap.Width, bitmap.Height);
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
