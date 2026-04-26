using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using SMMonitor.Common;

namespace SMMonitor.Agent.Manager;

public static class ManagerCommandServer
{
    public static async Task RunAsync(Action<string> log, CancellationToken token)
    {
        var pipeName = AgentSettings.ManagerPipeName;
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

                    if (!IsCommandRequest(line))
                    {
                        log(line);
                        continue;
                    }

                    var action = ExtractAction(line);
                    var result = HandleRequest(line);
                    await writer.WriteLineAsync(JsonSerializer.Serialize(result));
                    if (string.Equals(action, "start_process", StringComparison.OrdinalIgnoreCase))
                    {
                        log(result.Ok
                            ? $"[ManagerCmd] start_process ok pid={result.ProcessId}"
                            : $"[ManagerCmd] start_process failed: {result.Error}");
                    }
                    else
                    {
                        log(result.Ok
                            ? $"[ManagerCmd] capture ok {result.Width}x{result.Height}"
                            : $"[ManagerCmd] capture failed: {result.Error}");
                    }
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

    private static bool IsCommandRequest(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.TryGetProperty("action", out var actionEl) &&
                   !string.IsNullOrWhiteSpace(actionEl.GetString());
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractAction(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            return root.TryGetProperty("action", out var actionEl) ? (actionEl.GetString() ?? "") : "";
        }
        catch
        {
            return "";
        }
    }

    private static ManagerCaptureResponse HandleRequest(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var action = root.TryGetProperty("action", out var actionEl) ? (actionEl.GetString() ?? "") : "";
            return action.ToLowerInvariant() switch
            {
                "capture_screen" => CapturePrimaryScreen(
                    root.TryGetProperty("imageFormat", out var fmt) ? (fmt.GetString() ?? "jpeg") : "jpeg",
                    root.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv) ? Math.Clamp(qv, 30, 100) : 70),
                "capture_app" => CaptureAppWindow(
                    root.TryGetProperty("appName", out var an) ? (an.GetString() ?? "") : "",
                    root.TryGetProperty("imageFormat", out var af) ? (af.GetString() ?? "jpeg") : "jpeg",
                    root.TryGetProperty("quality", out var aq) && aq.TryGetInt32(out var aqv) ? Math.Clamp(aqv, 30, 100) : 70),
                "start_process" => StartProcessInManagerSession(
                    root.TryGetProperty("filePath", out var fp) ? (fp.GetString() ?? "") : "",
                    root.TryGetProperty("arguments", out var arg) ? (arg.GetString() ?? "") : ""),
                _ => ManagerCaptureResponse.Fail($"unsupported action: {action}")
            };
        }
        catch (Exception ex)
        {
            return ManagerCaptureResponse.Fail(ex.Message);
        }
    }

    private static ManagerCaptureResponse StartProcessInManagerSession(string filePath, string arguments)
    {
        filePath = (filePath ?? "").Trim();
        if (filePath.Length == 0)
        {
            return ManagerCaptureResponse.Fail("filePath required");
        }

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = filePath,
            Arguments = arguments ?? "",
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(filePath) ?? Environment.CurrentDirectory
        });

        if (process == null)
        {
            return ManagerCaptureResponse.Fail("start process failed");
        }

        return new ManagerCaptureResponse
        {
            Ok = true,
            Error = null,
            Message = "started in manager session",
            ProcessId = process.Id
        };
    }

    private static ManagerCaptureResponse CapturePrimaryScreen(string imageFormat, int jpegQuality)
    {
        var bounds = Screen.PrimaryScreen?.Bounds;
        if (bounds == null || bounds.Value.Width <= 0 || bounds.Value.Height <= 0)
        {
            return ManagerCaptureResponse.Fail("manager screen is not available");
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

        return new ManagerCaptureResponse
        {
            Ok = true,
            Error = null,
            ContentType = contentType,
            ImageBase64 = Convert.ToBase64String(ms.ToArray()),
            Width = bitmap.Width,
            Height = bitmap.Height
        };
    }

    private static ManagerCaptureResponse CaptureAppWindow(string appName, string imageFormat, int jpegQuality)
    {
        var target = FindMainWindowProcess(appName);
        if (target == null)
        {
            return ManagerCaptureResponse.Fail($"app window not found: {appName}");
        }

        if (target.MainWindowHandle == IntPtr.Zero || !GetWindowRect(target.MainWindowHandle, out var rect))
        {
            return ManagerCaptureResponse.Fail($"app main window unavailable: {appName}");
        }

        var width = Math.Max(0, rect.Right - rect.Left);
        var height = Math.Max(0, rect.Bottom - rect.Top);
        if (width <= 0 || height <= 0)
        {
            return ManagerCaptureResponse.Fail($"app window rect invalid: {appName}");
        }

        using var bitmap = new Bitmap(width, height);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, bitmap.Size);
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

        return new ManagerCaptureResponse
        {
            Ok = true,
            Error = null,
            ContentType = contentType,
            ImageBase64 = Convert.ToBase64String(ms.ToArray()),
            Width = bitmap.Width,
            Height = bitmap.Height,
            Message = $"captured app window: {target.ProcessName}"
        };
    }

    private static Process? FindMainWindowProcess(string appName)
    {
        var normalized = (appName ?? "").Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        if (normalized.Length == 0)
        {
            return null;
        }

        return Process.GetProcessesByName(normalized)
            .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
}

public sealed class ManagerCaptureResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? Message { get; set; }
    public int ProcessId { get; set; }
    public string ContentType { get; set; } = "image/jpeg";
    public string ImageBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }

    public static ManagerCaptureResponse Fail(string message)
    {
        return new ManagerCaptureResponse
        {
            Ok = false,
            Error = message,
            ContentType = "image/jpeg",
            ImageBase64 = "",
            Width = 0,
            Height = 0
        };
    }
}
