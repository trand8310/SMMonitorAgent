using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SMMonitor.Common;

namespace SMMonitor.Agent.Service;

public sealed class WsMonitorAgent
{
    private readonly AgentSettings _settings;
    private readonly ILogger _logger;
    private readonly ResourceCollector _collector = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly Dictionary<string, bool> _appRunningState = new(StringComparer.OrdinalIgnoreCase);

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public WsMonitorAgent(AgentSettings settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var ws = new ClientWebSocket();
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        _logger.LogInformation("Connecting websocket: {Url}", _settings.ServerUrl);

        await ws.ConnectAsync(new Uri(_settings.ServerUrl), token);

        _logger.LogInformation("WebSocket connected.");

        AgentConfigStore.SaveStatus(new AgentStatus
        {
            ClientId = _settings.ClientId,
            ServiceRunning = true,
            WsConnected = true,
            LastUploadTime = DateTime.Now,
            LastError = "",
            ServerUrl = _settings.ServerUrl
        });

        var receiveTask = ReceiveLoopAsync(ws, token);
        var uploadTask = UploadLoopAsync(ws, token);

        await Task.WhenAny(receiveTask, uploadTask);

        try
        {
            if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "agent closing", CancellationToken.None);
            }
        }
        catch
        {
        }
    }

    private async Task UploadLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                var snapshot = _collector.Collect(_settings.Version, _settings.MonitoredApps);
                var diskMax = snapshot.Disks.Count > 0 ? snapshot.Disks.Max(x => x.UsedPercent) : 0;

                var msg = new
                {
                    type = "monitor",
                    clientId = _settings.ClientId,
                    token = _settings.Token,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    payload = snapshot
                };

                await SendJsonAsync(ws, msg, token);
                await PublishAppAlertsIfNeededAsync(ws, snapshot, token);

                AgentConfigStore.SaveStatus(new AgentStatus
                {
                    ClientId = _settings.ClientId,
                    ServiceRunning = true,
                    WsConnected = true,
                    LastUploadTime = DateTime.Now,
                    LastError = "",
                    Cpu = snapshot.Cpu,
                    MemoryUsedPercent = snapshot.MemoryUsedPercent,
                    DiskMaxUsedPercent = diskMax,
                    ServerUrl = _settings.ServerUrl
                });
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Upload monitor failed.");

                AgentConfigStore.SaveStatus(new AgentStatus
                {
                    ClientId = _settings.ClientId,
                    ServiceRunning = true,
                    WsConnected = false,
                    LastError = "upload failed: " + ex.Message,
                    LastUploadTime = DateTime.Now,
                    ServerUrl = _settings.ServerUrl
                });

                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _settings.UploadIntervalSeconds)), token);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];

        while (!token.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            try
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;

                do
                {
                    result = await ws.ReceiveAsync(buffer, token);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogWarning("WebSocket closed by server.");
                        return;
                    }

                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(ms.ToArray());
                await HandleServerMessageAsync(ws, json, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Receive websocket message failed.");
                break;
            }
        }
    }

    private async Task HandleServerMessageAsync(ClientWebSocket ws, string json, CancellationToken token)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : "";
            if (!string.Equals(type, "request", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var requestId = root.TryGetProperty("requestId", out var reqEl) ? reqEl.GetString() ?? "" : "";
            var action = root.TryGetProperty("action", out var actionEl) ? actionEl.GetString() ?? "" : "";

            AgentConfigStore.SaveStatus(new AgentStatus
            {
                ClientId = _settings.ClientId,
                ServiceRunning = true,
                WsConnected = true,
                LastUploadTime = DateTime.Now,
                LastCommand = action,
                ServerUrl = _settings.ServerUrl
            });

            switch (action.ToLowerInvariant())
            {
                case "ping":
                    await SendResponseAsync(ws, requestId, true, "pong", token);
                    break;

                case "get_config":
                    await SendJsonAsync(ws, new
                    {
                        type = "response",
                        requestId,
                        clientId = _settings.ClientId,
                        ok = true,
                        msg = "config",
                        payload = new
                        {
                            _settings.ServerUrl,
                            _settings.ClientId,
                            _settings.Version,
                            _settings.UploadIntervalSeconds,
                            _settings.EnableUpload,
                            _settings.EnableRemoteReboot,
                            _settings.CpuAlertPercent,
                            _settings.MemoryAlertPercent,
                            _settings.DiskAlertPercent
                        },
                        ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }, token);
                    break;

                case "reboot":
                    await HandleRebootAsync(ws, requestId, root, token);
                    break;

                case "app_status":
                    await HandleAppStatusAsync(ws, requestId, token);
                    break;

                case "screen_screenshot":
                    await HandleScreenScreenshotAsync(ws, requestId, root, token);
                    break;

                case "app_screenshot":
                    await HandleAppScreenshotAsync(ws, requestId, root, token);
                    break;

                default:
                    await SendResponseAsync(ws, requestId, false, $"unknown action: {action}", token);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handle server message failed. Raw={Json}", json);
        }
    }

    private async Task HandleAppStatusAsync(ClientWebSocket ws, string requestId, CancellationToken token)
    {
        var statuses = _collector.Collect(_settings.Version, _settings.MonitoredApps).MonitoredApps;

        await SendJsonAsync(ws, new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok = true,
            msg = "app status",
            data = new
            {
                monitoredApps = statuses
            },
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);
    }

    private async Task HandleScreenScreenshotAsync(ClientWebSocket ws, string requestId, JsonElement root, CancellationToken token)
    {
        var imageFormat = "jpeg";
        var quality = 70;

        if (root.TryGetProperty("payload", out var payload))
        {
            imageFormat = payload.TryGetProperty("imageFormat", out var fmt) ? (fmt.GetString() ?? "jpeg") : "jpeg";
            if (payload.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv))
            {
                quality = Math.Clamp(qv, 30, 100);
            }
        }

        var screenshot = ScreenshotHelper.TryCapturePrimaryScreen(imageFormat, quality);
        if (!screenshot.Ok)
        {
            await SendResponseAsync(ws, requestId, false, screenshot.Error ?? "capture screen failed", token);
            return;
        }

        await SendJsonAsync(ws, new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok = true,
            msg = "screen screenshot",
            data = screenshot.ToData(),
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);
    }

    private async Task HandleAppScreenshotAsync(ClientWebSocket ws, string requestId, JsonElement root, CancellationToken token)
    {
        var appName = "";
        var imageFormat = "jpeg";
        var quality = 70;

        if (root.TryGetProperty("payload", out var payload))
        {
            appName = payload.TryGetProperty("name", out var name) ? (name.GetString() ?? "") : "";
            imageFormat = payload.TryGetProperty("imageFormat", out var fmt) ? (fmt.GetString() ?? "jpeg") : "jpeg";

            if (payload.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv))
            {
                quality = Math.Clamp(qv, 30, 100);
            }
        }

        var screenshot = ScreenshotHelper.TryCapturePrimaryScreen(imageFormat, quality);
        if (!screenshot.Ok)
        {
            await SendResponseAsync(ws, requestId, false, screenshot.Error ?? "capture app failed", token);
            return;
        }

        await SendJsonAsync(ws, new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok = true,
            msg = "app screenshot",
            data = new
            {
                appName,
                captureMode = "screen-fallback",
                imageBase64 = screenshot.ImageBase64,
                contentType = screenshot.ContentType,
                width = screenshot.Width,
                height = screenshot.Height
            },
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);
    }

    private async Task PublishAppAlertsIfNeededAsync(ClientWebSocket ws, MonitorSnapshot snapshot, CancellationToken token)
    {
        foreach (var app in snapshot.MonitoredApps)
        {
            var current = app.IsRunning;
            if (!_appRunningState.TryGetValue(app.Name, out var old))
            {
                _appRunningState[app.Name] = current;
                continue;
            }

            if (old == current)
            {
                continue;
            }

            _appRunningState[app.Name] = current;

            if (current)
            {
                await SendJsonAsync(ws, new
                {
                    type = "app_alert",
                    level = "info",
                    clientId = _settings.ClientId,
                    token = _settings.Token,
                    ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    payload = new
                    {
                        app = app.Name,
                        status = "recovered",
                        processCount = app.ProcessCount,
                        message = $"monitored app recovered: {app.Name}"
                    }
                }, token);
                continue;
            }

            var screenshot = _settings.AutoCaptureScreenshotOnAppFailure
                ? ScreenshotHelper.TryCapturePrimaryScreen("jpeg", 60)
                : ScreenshotCaptureResult.NotEnabled();

            await SendJsonAsync(ws, new
            {
                type = "app_alert",
                level = "critical",
                clientId = _settings.ClientId,
                token = _settings.Token,
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                payload = new
                {
                    app = app.Name,
                    status = "stopped",
                    processCount = app.ProcessCount,
                    message = $"monitored app stopped: {app.Name}",
                    screenshot = screenshot.ToData()
                }
            }, token);
        }
    }

    private async Task HandleRebootAsync(ClientWebSocket ws, string requestId, JsonElement root, CancellationToken token)
    {
        if (!_settings.EnableRemoteReboot)
        {
            await SendResponseAsync(ws, requestId, false, "remote reboot disabled", token);
            return;
        }

        var delaySeconds = 5;
        var reason = "remote reboot";

        if (root.TryGetProperty("payload", out var payload))
        {
            if (payload.TryGetProperty("delaySeconds", out var d) && d.TryGetInt32(out var delay))
            {
                delaySeconds = Math.Clamp(delay, 0, 3600);
            }

            if (payload.TryGetProperty("reason", out var r))
            {
                reason = r.GetString() ?? reason;
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "shutdown.exe",
                Arguments = $"/r /t {delaySeconds} /c \"{reason.Replace("\"", "'")}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            });

            await SendResponseAsync(ws, requestId, true, "reboot command accepted", token);
        }
        catch (Exception ex)
        {
            await SendResponseAsync(ws, requestId, false, ex.Message, token);
        }
    }

    private async Task SendResponseAsync(ClientWebSocket ws, string requestId, bool ok, string msg, CancellationToken token)
    {
        var res = new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok,
            msg,
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        await SendJsonAsync(ws, res, token);
    }

    private async Task SendJsonAsync(ClientWebSocket ws, object data, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(data, _jsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);

        await _sendLock.WaitAsync(token);

        try
        {
            if (ws.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("websocket is not open");
            }

            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
