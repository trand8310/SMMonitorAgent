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
    private AppPipeForwarder? _pipeForwarder;
    private volatile bool _pipeLivePushEnabled;
    private volatile string _pipeLivePushApp = "";

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
        await ManagerPipePublisher.TryPublishAsync("Service", "WsMonitorAgent", "WebSocket connected", token);

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
        var pipeTask = PipeForwardLoopAsync(ws, token);

        await Task.WhenAny(receiveTask, uploadTask, pipeTask);

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

    private async Task PipeForwardLoopAsync(ClientWebSocket ws, CancellationToken token)
    {
        if (!_settings.EnablePipeForward || string.IsNullOrWhiteSpace(_settings.AppPipeName))
        {
            return;
        }

        _pipeForwarder ??= new AppPipeForwarder(_settings.AppPipeName, _logger);

        await _pipeForwarder.RunAsync(async message =>
        {
            if (ws.State != WebSocketState.Open)
            {
                return;
            }

            if (_pipeLivePushEnabled == false)
            {
                return;
            }

            if (IsPipeMessageAllowed(message.Content) == false)
            {
                return;
            }

            await SendJsonAsync(ws, new
            {
                type = "app_pipe_message",
                clientId = _settings.ClientId,
                token = _settings.Token,
                ts = message.Timestamp,
                payload = new
                {
                    pipeName = message.PipeName,
                    content = message.Content
                }
            }, token);
            await ManagerPipePublisher.TryPublishAsync("App", "PipeForward", message.Content, token);
        }, token);
    }

    private bool IsPipeMessageAllowed(string content)
    {
        if (string.IsNullOrWhiteSpace(_pipeLivePushApp))
        {
            return true;
        }

        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;
            var app = root.TryGetProperty("app", out var appEl) ? appEl.GetString() :
                root.TryGetProperty("appName", out var appNameEl) ? appNameEl.GetString() : "";

            if (string.IsNullOrWhiteSpace(app))
            {
                return false;
            }

            return string.Equals(NormalizeProcessName(app), _pipeLivePushApp, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
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
            await ManagerPipePublisher.TryPublishAsync("Service", "WsRequest", $"action={action}, requestId={requestId}", token);

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
                            _settings.EnablePipeForward,
                            _settings.AppPipeName,
                            PipeLivePushEnabled = _pipeLivePushEnabled,
                            PipeLivePushApp = _pipeLivePushApp,
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

                case "command":
                    await HandleCommandAsync(ws, requestId, root, token);
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
                monitoredApps = statuses,
                monitoredAppProfiles = _settings.MonitoredAppProfiles
            },
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);
    }

    private async Task HandleCommandAsync(ClientWebSocket ws, string requestId, JsonElement root, CancellationToken token)
    {
        var command = "";
        JsonElement args = default;

        if (root.TryGetProperty("payload", out var payload))
        {
            command = payload.TryGetProperty("command", out var c) ? (c.GetString() ?? "") : "";
            args = payload.TryGetProperty("args", out var a) ? a : default;
        }

        await ManagerPipePublisher.TryPublishAsync("Service", "Command", $"requestId={requestId}, command={command}", token);

        switch (command.ToLowerInvariant())
        {
            case "app_start":
                await HandleAppStartAsync(ws, requestId, args, token);
                return;
            case "app_stop":
                await HandleAppStopAsync(ws, requestId, args, token);
                return;
            case "app_screenshot":
                await HandleAppScreenshotAsync(ws, requestId, root, token);
                return;
            case "screen_screenshot":
                await HandleScreenScreenshotAsync(ws, requestId, root, token);
                return;
            case "pipe_live_push":
                await HandlePipeLivePushAsync(ws, requestId, args, token);
                return;
            default:
                await SendResponseAsync(ws, requestId, false, $"unknown command: {command}", token);
                return;
        }
    }

    private async Task HandlePipeLivePushAsync(ClientWebSocket ws, string requestId, JsonElement args, CancellationToken token)
    {
        var enabled = args.ValueKind == JsonValueKind.Object &&
                      args.TryGetProperty("enabled", out var enabledEl) &&
                      enabledEl.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                      enabledEl.GetBoolean();

        var appName = args.ValueKind == JsonValueKind.Object &&
                      args.TryGetProperty("appName", out var appNameEl)
            ? NormalizeProcessName(appNameEl.GetString() ?? "")
            : "";

        _pipeLivePushEnabled = enabled;
        _pipeLivePushApp = appName;

        await SendJsonAsync(ws, new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok = true,
            msg = enabled ? "pipe live push enabled" : "pipe live push disabled",
            data = new
            {
                enabled = _pipeLivePushEnabled,
                appName = _pipeLivePushApp
            },
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);
    }

    private async Task HandleAppStartAsync(ClientWebSocket ws, string requestId, JsonElement args, CancellationToken token)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var filePath = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("filePath", out var filePathEl) ? (filePathEl.GetString() ?? "") : "";
        var appArgs = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("arguments", out var a) ? (a.GetString() ?? "") : "";

        if (string.IsNullOrWhiteSpace(filePath))
        {
            var profile = FindProfileByName(name);
            if (profile != null)
            {
                filePath = profile.FilePath;
                if (string.IsNullOrWhiteSpace(appArgs))
                {
                    appArgs = profile.Arguments;
                }
            }
        }

        try
        {
            Process? startedProcess = null;
            var launchTarget = filePath;

            if (string.IsNullOrWhiteSpace(launchTarget))
            {
                launchTarget = name.Trim();
            }

            if (string.IsNullOrWhiteSpace(launchTarget))
            {
                await ManagerPipePublisher.TryPublishAsync("Service", "AppStart", "failed: filePath/name both empty", token);
                await SendResponseAsync(ws, requestId, false, "filePath or name required", token);
                return;
            }

            startedProcess = TryStartProcess(launchTarget, appArgs);

            if (startedProcess == null && !launchTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                launchTarget += ".exe";
                startedProcess = TryStartProcess(launchTarget, appArgs);
            }

            await SendJsonAsync(ws, new
            {
                type = "response",
                requestId,
                clientId = _settings.ClientId,
                ok = startedProcess != null,
                msg = startedProcess != null ? "app started" : "start failed",
                data = new
                {
                    name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(launchTarget) : name,
                    filePath = launchTarget,
                    arguments = appArgs,
                    processId = startedProcess?.Id ?? 0
                },
                ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            }, token);

            await ManagerPipePublisher.TryPublishAsync(
                "Service",
                "AppStart",
                startedProcess != null
                    ? $"success: target={launchTarget}, pid={startedProcess.Id}"
                    : $"failed: target={launchTarget}, args={appArgs}",
                token);
        }
        catch (Exception ex)
        {
            await ManagerPipePublisher.TryPublishAsync("Service", "AppStart", $"exception: {ex.Message}", token);
            await SendResponseAsync(ws, requestId, false, ex.Message, token);
        }
    }

    private async Task HandleAppStopAsync(ClientWebSocket ws, string requestId, JsonElement args, CancellationToken token)
    {
        var name = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        var processId = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("processId", out var p) && p.TryGetInt32(out var pid) ? pid : 0;

        var killed = 0;
        var errors = new List<string>();

        if (processId > 0)
        {
            try
            {
                using var proc = Process.GetProcessById(processId);
                proc.Kill(true);
                killed++;
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                await SendResponseAsync(ws, requestId, false, "name or processId required", token);
                return;
            }

            var processName = NormalizeProcessName(name);
            foreach (var proc in Process.GetProcessesByName(processName))
            {
                try
                {
                    proc.Kill(true);
                    killed++;
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                }
                finally
                {
                    proc.Dispose();
                }
            }
        }

        await SendJsonAsync(ws, new
        {
            type = "response",
            requestId,
            clientId = _settings.ClientId,
            ok = killed > 0 && errors.Count == 0,
            msg = killed > 0 ? $"stopped {killed} process(es)" : "no process stopped",
            data = new
            {
                name,
                processId,
                killedCount = killed,
                errors
            },
            ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }, token);

        await ManagerPipePublisher.TryPublishAsync(
            "Service",
            "AppStop",
            $"name={name}, pid={processId}, killed={killed}, errors={errors.Count}",
            token);
    }

    private static Process? TryStartProcess(string fileName, string arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? Environment.CurrentDirectory,
            UseShellExecute = true
        };
        return Process.Start(info);
    }

    private MonitoredAppProfile? FindProfileByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var normalized = NormalizeProcessName(name);
        return _settings.MonitoredAppProfiles.FirstOrDefault(x => string.Equals(x.Name, normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeProcessName(string raw)
    {
        var value = raw.Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }
        return value;
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

        var screenshot = await TryCaptureScreenAsync(imageFormat, quality, token);
        if (!screenshot.Ok)
        {
            await ManagerPipePublisher.TryPublishAsync(
                "Service",
                "ScreenShot",
                $"scope=screen, ok=false, error={screenshot.Error}",
                token);
            await SendResponseAsync(ws, requestId, false, screenshot.Error ?? "capture screen failed", token);
            return;
        }

        await ManagerPipePublisher.TryPublishAsync(
            "Service",
            "ScreenShot",
            $"scope=screen, ok=true, width={screenshot.Width}, height={screenshot.Height}, contentType={screenshot.ContentType}",
            token);

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
            if (string.IsNullOrWhiteSpace(appName) &&
                payload.TryGetProperty("args", out var args) &&
                args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty("name", out var argsName))
            {
                appName = argsName.GetString() ?? "";
            }
            imageFormat = payload.TryGetProperty("imageFormat", out var fmt) ? (fmt.GetString() ?? "jpeg") : "jpeg";

            if (payload.TryGetProperty("quality", out var q) && q.TryGetInt32(out var qv))
            {
                quality = Math.Clamp(qv, 30, 100);
            }
        }

        var screenshot = await TryCaptureScreenAsync(imageFormat, quality, token);
        if (!screenshot.Ok)
        {
            await ManagerPipePublisher.TryPublishAsync(
                "Service",
                "ScreenShot",
                $"scope=app, app={appName}, ok=false, error={screenshot.Error}",
                token);
            await SendResponseAsync(ws, requestId, false, screenshot.Error ?? "capture app failed", token);
            return;
        }

        await ManagerPipePublisher.TryPublishAsync(
            "Service",
            "ScreenShot",
            $"scope=app, app={appName}, ok=true, width={screenshot.Width}, height={screenshot.Height}, contentType={screenshot.ContentType}",
            token);

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

    private static async Task<ScreenshotCaptureResult> TryCaptureScreenAsync(string imageFormat, int quality, CancellationToken token)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var local = await Task.Run(
                () => ScreenshotHelper.TryCapturePrimaryScreen(imageFormat, quality),
                timeoutCts.Token);
            if (local.Ok)
            {
                return local;
            }

            if (!ShouldTryManagerCapture(local.Error))
            {
                return local;
            }

            var manager = await ManagerScreenshotBridge.TryCaptureAsync(imageFormat, quality, token);
            if (manager.Ok)
            {
                return manager;
            }

            return new ScreenshotCaptureResult
            {
                Ok = false,
                Error = $"local capture failed: {local.Error}; manager fallback failed: {manager.Error}"
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            var manager = await ManagerScreenshotBridge.TryCaptureAsync(imageFormat, quality, token);
            return manager.Ok
                ? manager
                : new ScreenshotCaptureResult
                {
                    Ok = false,
                    Error = $"capture timeout and manager fallback failed: {manager.Error}"
                };
        }
    }

    private static bool ShouldTryManagerCapture(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("non-interactive", StringComparison.OrdinalIgnoreCase)
               || error.Contains("session 0", StringComparison.OrdinalIgnoreCase)
               || error.Contains("screen is not available", StringComparison.OrdinalIgnoreCase)
               || error.Contains("capture timeout", StringComparison.OrdinalIgnoreCase);
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
