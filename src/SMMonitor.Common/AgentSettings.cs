namespace SMMonitor.Common;

public sealed class AgentSettings
{
    // 应用程序 -> Agent 的固定管道名
    public const string FixedPipeName = "SMMONITOR_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
    // Service -> Manager 日志/通知管道名
    public const string ManagerPipeName = "SMMANAGER_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
    // Service <-> Manager 命令/数据交互管道名（当前与 ManagerPipeName 复用同一条双工管道）
    public const string ServicePipeName = ManagerPipeName;
    public string ServerUrl { get; set; } = "ws://117.21.200.221:9502";
    public string Token { get; set; } = "ce83aaac";
    public string ClientId { get; set; } = "";
    public string Version { get; set; } = "1.0.0";

    public int UploadIntervalSeconds { get; set; } = 5;
    public bool EnableUpload { get; set; } = true;
    public bool EnableRemoteReboot { get; set; } = false;

    public int CpuAlertPercent { get; set; } = 95;
    public int MemoryAlertPercent { get; set; } = 90;
    public int DiskAlertPercent { get; set; } = 90;

    /// <summary>
    /// 需要监控的应用进程名列表（不区分大小写，支持不带 .exe）。
    /// 例如：["notepad", "chrome", "MyApp.exe"]
    /// </summary>
    public List<string> MonitoredApps { get; set; } = new();

    /// <summary>
    /// 监控应用完整定义，支持完整路径和默认启动参数。
    /// </summary>
    public List<MonitoredAppProfile> MonitoredAppProfiles { get; set; } = new();

    /// <summary>
    /// 监控应用异常时，是否自动尝试采集屏幕截图并随告警一起上报。
    /// </summary>
    public bool AutoCaptureScreenshotOnAppFailure { get; set; } = false;

    /// <summary>
    /// 应用推送实时消息的命名管道标识（名称）。
    /// </summary>
    public string AppPipeName { get; set; } = FixedPipeName;

    /// <summary>
    /// 是否启用应用命名管道消息转发到管理后台。
    /// </summary>
    public bool EnablePipeForward { get; set; } = false;
}

public sealed class MonitoredAppProfile
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Arguments { get; set; } = "";
}

public sealed class AgentStatus
{
    public string ClientId { get; set; } = "";
    public bool ServiceRunning { get; set; }
    public bool WsConnected { get; set; }
    public DateTime LastUploadTime { get; set; }
    public string LastError { get; set; } = "";
    public string LastCommand { get; set; } = "";

    public double Cpu { get; set; }
    public double MemoryUsedPercent { get; set; }
    public double DiskMaxUsedPercent { get; set; }
    public string ServerUrl { get; set; } = "";
}
