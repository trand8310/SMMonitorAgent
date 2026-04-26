using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using SMMonitor.Common;

namespace SMMonitor.Agent.Service;

public static class ManagerScreenshotBridge
{
    public static async Task<ScreenshotCaptureResult> TryCaptureAsync(string imageFormat, int jpegQuality, CancellationToken token)
    {
        try
        {
            var pipeName = AgentSettings.ManagerPipeName;
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1200, token);
            if (!pipe.IsConnected)
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "manager command pipe not connected" };
            }

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);

            var reqJson = JsonSerializer.Serialize(new
            {
                action = "capture_screen",
                imageFormat,
                quality = Math.Clamp(jpegQuality, 30, 100)
            });

            await writer.WriteLineAsync(reqJson);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "manager screenshot response empty" };
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ok = TryGetBoolean(root, "ok") ?? TryGetBoolean(root, "Ok") ?? false;
            if (!ok)
            {
                var err = TryGetString(root, "error") ?? TryGetString(root, "Error");
                return new ScreenshotCaptureResult { Ok = false, Error = err ?? "manager screenshot failed" };
            }

            return new ScreenshotCaptureResult
            {
                Ok = true,
                Error = null,
                ImageBase64 = TryGetString(root, "imageBase64") ?? TryGetString(root, "ImageBase64") ?? "",
                ContentType = TryGetString(root, "contentType") ?? TryGetString(root, "ContentType") ?? "image/jpeg",
                Width = TryGetInt32(root, "width") ?? TryGetInt32(root, "Width") ?? 0,
                Height = TryGetInt32(root, "height") ?? TryGetInt32(root, "Height") ?? 0
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new ScreenshotCaptureResult { Ok = false, Error = "manager screenshot timeout" };
        }
        catch (Exception ex)
        {
            return new ScreenshotCaptureResult { Ok = false, Error = $"manager screenshot unavailable: {ex.Message}" };
        }
    }

    public static async Task<ScreenshotCaptureResult> TryCaptureAppAsync(string appName, string imageFormat, int jpegQuality, CancellationToken token)
    {
        try
        {
            var pipeName = AgentSettings.ManagerPipeName;
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1200, token);
            if (!pipe.IsConnected)
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "manager command pipe not connected" };
            }

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);

            var reqJson = JsonSerializer.Serialize(new
            {
                action = "capture_app",
                appName,
                imageFormat,
                quality = Math.Clamp(jpegQuality, 30, 100)
            });

            await writer.WriteLineAsync(reqJson);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                return new ScreenshotCaptureResult { Ok = false, Error = "manager app screenshot response empty" };
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ok = TryGetBoolean(root, "ok") ?? TryGetBoolean(root, "Ok") ?? false;
            if (!ok)
            {
                var err = TryGetString(root, "error") ?? TryGetString(root, "Error");
                return new ScreenshotCaptureResult { Ok = false, Error = err ?? "manager app screenshot failed" };
            }

            return new ScreenshotCaptureResult
            {
                Ok = true,
                Error = null,
                ImageBase64 = TryGetString(root, "imageBase64") ?? TryGetString(root, "ImageBase64") ?? "",
                ContentType = TryGetString(root, "contentType") ?? TryGetString(root, "ContentType") ?? "image/jpeg",
                Width = TryGetInt32(root, "width") ?? TryGetInt32(root, "Width") ?? 0,
                Height = TryGetInt32(root, "height") ?? TryGetInt32(root, "Height") ?? 0
            };
        }
        catch (Exception ex)
        {
            return new ScreenshotCaptureResult { Ok = false, Error = $"manager app screenshot unavailable: {ex.Message}" };
        }
    }

    public static async Task<(bool ok, string message, int processId)> TryStartProcessAsync(string filePath, string arguments, CancellationToken token)
    {
        try
        {
            var pipeName = AgentSettings.ManagerPipeName;
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1200, token);
            if (!pipe.IsConnected)
            {
                return (false, "manager command pipe not connected", 0);
            }

            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, true, leaveOpen: true);

            var reqJson = JsonSerializer.Serialize(new
            {
                action = "start_process",
                filePath,
                arguments = arguments ?? ""
            });

            await writer.WriteLineAsync(reqJson);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var line = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(line))
            {
                return (false, "manager start process response empty", 0);
            }

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var ok = TryGetBoolean(root, "ok") ?? TryGetBoolean(root, "Ok") ?? false;
            var msg = TryGetString(root, "message") ?? TryGetString(root, "Message")
                      ?? TryGetString(root, "error") ?? TryGetString(root, "Error")
                      ?? (ok ? "started in manager session" : "manager start process failed");
            var pid = TryGetInt32(root, "processId") ?? TryGetInt32(root, "ProcessId") ?? 0;
            return (ok, msg, pid);
        }
        catch (Exception ex)
        {
            return (false, $"manager start process unavailable: {ex.Message}", 0);
        }
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) ? el.GetString() : null;
    }

    private static bool? TryGetBoolean(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) ? el.GetBoolean() : null;
    }

    private static int? TryGetInt32(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var el) && el.TryGetInt32(out var value) ? value : null;
    }
}
