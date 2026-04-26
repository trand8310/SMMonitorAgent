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
            var pipeName = AgentSettings.FixedPipeName + "_manager_cmd";
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
            var ok = root.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                var err = root.TryGetProperty("error", out var errEl) ? errEl.GetString() : null;
                return new ScreenshotCaptureResult { Ok = false, Error = err ?? "manager screenshot failed" };
            }

            return new ScreenshotCaptureResult
            {
                Ok = true,
                Error = null,
                ImageBase64 = root.TryGetProperty("imageBase64", out var b64El) ? (b64El.GetString() ?? "") : "",
                ContentType = root.TryGetProperty("contentType", out var ctEl) ? (ctEl.GetString() ?? "image/jpeg") : "image/jpeg",
                Width = root.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0,
                Height = root.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0
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
}
