# SMMonitorAgent

.NET 8 Windows 系统资源监控 Agent 示例项目。

包含：

- `SMMonitor.Agent.Service`：Windows 服务，后台采集 CPU、内存、磁盘信息，通过 WebSocket 上报服务器，并接收远程命令。
- `SMMonitor.Agent.Manager`：WinForms 管理配置界面，修改 WS 地址、Token、ClientId、上报间隔、远程重启开关，并可启动/停止/重启服务。
- `SMMonitor.Common`：公共配置和状态文件读写库。

## 新增：指定应用监控与异常主动上报

支持在配置中设置 `MonitoredApps`（进程名列表，不区分大小写，不必带 `.exe`）。

- Agent 在每次 `monitor` 上报时附带 `payload.monitoredApps` 运行状态。
- 多进程应用会按进程名聚合统计，不会漏报；同名进程会合并计算 `cpuPercent / memoryUsedMb / threadCount / processCount`。
- 建议在配置中维护 `MonitoredAppProfiles`（`名称|完整路径|默认参数`），便于远程启动时直接使用完整路径和启动参数。
- 支持应用通过命名管道把实时状态/日志推送给 Agent，再由 Agent 转发到后台管理页面（`index.html` 可直接查看最近消息）。
- 监控应用状态变化（运行→停止、停止→恢复）时，会主动上报 `type=app_alert` 事件。
- 配置 `AutoCaptureScreenshotOnAppFailure=true` 时，应用异常告警会尝试附带截图（受 Windows 服务 Session 0 隔离影响，部分机器可能不可见）。

## 目录结构

```text
SMMonitorAgent
├─ SMMonitorAgent.sln
├─ src
│  ├─ SMMonitor.Common
│  ├─ SMMonitor.Agent.Service
│  └─ SMMonitor.Agent.Manager
└─ scripts
   ├─ publish-win-x64.bat
   ├─ install-service-admin.bat
   ├─ uninstall-service-admin.bat
   └─ restart-service-admin.bat
```

## 配置文件位置

服务和管理界面共用配置文件：

```text
C:\ProgramData\SMMonitorAgent\agentsettings.json
C:\ProgramData\SMMonitorAgent\status.json
```

## 使用步骤

1. 用 Visual Studio 2022 打开 `SMMonitorAgent.sln`。
2. 修改或通过管理器配置：
   - `ServerUrl`：例如 `ws://你的服务器IP:9502`
   - `Token`：和服务端一致
   - `ClientId`：默认取本机局域网 IP
3. 右键用管理员身份运行 `scripts\publish-win-x64.bat` 发布。
4. 右键用管理员身份运行 `scripts\install-service-admin.bat` 安装并启动 Windows 服务。
5. 打开 `publish\manager\SMMonitor.Agent.Manager.exe` 修改配置和查看状态。

## 客户端上报 JSON

```json
{
  "type": "monitor",
  "clientId": "192.168.1.10",
  "token": "your-token",
  "ts": 1710000000,
  "payload": {
    "machineName": "WIN-PC-01",
    "os": "Microsoft Windows 10",
    "version": "1.0.0",
    "cpu": 35.2,
    "memoryUsedPercent": 78.4,
    "memoryTotalMb": 16384,
    "memoryAvailableMb": 3538,
    "processUptimeSeconds": 3600,
    "bootTime": "2026-04-26T08:00:00",
    "disks": [
      {
        "name": "C:\\\\",
        "totalGb": 237.5,
        "freeGb": 52.1,
        "usedPercent": 78.1
      }
    ]
  }
}
```

## 服务端下发远程重启命令

客户端支持以下命令：

### ping

```json
{
  "type": "request",
  "requestId": "req_1",
  "action": "ping"
}
```

### get_config

```json
{
  "type": "request",
  "requestId": "req_2",
  "action": "get_config"
}
```

### reboot

需要在管理界面勾选“允许服务端远程重启本机”。

```json
{
  "type": "request",
  "requestId": "req_3",
  "action": "reboot",
  "payload": {
    "delaySeconds": 5,
    "reason": "memory too high"
  }
}
```

### app_status

```json
{
  "type": "request",
  "requestId": "req_4",
  "action": "app_status"
}
```

### app_screenshot / screen_screenshot

```json
{
  "type": "request",
  "requestId": "req_5",
  "action": "screen_screenshot",
  "payload": {
    "imageFormat": "jpeg",
    "quality": 70
  }
}
```

### command (app_start / app_stop)

远程启动应用（支持完整路径和参数）：

```json
{
  "type": "request",
  "requestId": "req_6",
  "action": "command",
  "payload": {
    "command": "app_start",
    "args": {
      "name": "MyApp",
      "filePath": "C:\\\\Apps\\\\MyApp\\\\MyApp.exe",
      "arguments": "--env=prod --port=9001"
    }
  }
}
```

远程停止应用（按 `processId` 或按 `name`）：

```json
{
  "type": "request",
  "requestId": "req_7",
  "action": "command",
  "payload": {
    "command": "app_stop",
    "args": {
      "name": "MyApp",
      "processId": 0
    }
  }
}
```

## 命名管道实时消息转发

在管理界面可配置：

- `EnablePipeForward`：是否启用管道转发。
- `AppPipeName`：管道标识（可点击生成 GUID 格式）。
- `pipe_live_push` 默认关闭，避免消息风暴；建议在 `index.html` 选择客户端与应用后动态打开。

应用端只需写入文本行到命名管道（UTF-8，每行一条），Agent 会转发为：

```json
{
  "type": "app_pipe_message",
  "clientId": "192.168.1.10",
  "token": "your-token",
  "ts": 1710000000,
  "payload": {
    "pipeName": "SMMONITOR_PIPE_xxx",
    "content": "业务状态: queue=21, workers=4"
  }
}
```

```json
{
  "type": "request",
  "requestId": "req_8",
  "action": "command",
  "payload": {
    "command": "pipe_live_push",
    "args": {
      "enabled": true,
      "appName": "MyApp"
    }
  }
}
```

## 注意事项

- Windows 服务不能直接弹出桌面 UI，所以管理界面是单独的 WinForms 程序。
- 管理器操作服务需要管理员权限，项目已设置 `requireAdministrator`。
- 远程重启默认关闭，需要在管理界面显式开启。
- 修改 WS 地址、Token、ClientId 后，建议点击“重启服务”。
- 建议正式环境使用 `wss://`，并给 HTML 管理后台加登录验证。
