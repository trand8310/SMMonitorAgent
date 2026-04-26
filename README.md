# SMMonitorAgent

.NET 8 Windows 系统资源监控 Agent 示例项目。

包含：

- `SMMonitor.Agent.Service`：Windows 服务，后台采集 CPU、内存、磁盘信息，通过 WebSocket 上报服务器，并接收远程命令。
- `SMMonitor.Agent.Manager`：WinForms 管理配置界面，修改 WS 地址、Token、ClientId、上报间隔、远程重启开关，并可启动/停止/重启服务。
- `SMMonitor.Common`：公共配置和状态文件读写库。

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

## 注意事项

- Windows 服务不能直接弹出桌面 UI，所以管理界面是单独的 WinForms 程序。
- 管理器操作服务需要管理员权限，项目已设置 `requireAdministrator`。
- 远程重启默认关闭，需要在管理界面显式开启。
- 修改 WS 地址、Token、ClientId 后，建议点击“重启服务”。
- 建议正式环境使用 `wss://`，并给 HTML 管理后台加登录验证。
