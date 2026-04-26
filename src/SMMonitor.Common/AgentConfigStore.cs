using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SMMonitor.Common;

public static class AgentConfigStore
{
    public static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SMMonitorAgent");

    public static readonly string ConfigFile = Path.Combine(BaseDir, "agentsettings.json");
    public static readonly string StatusFile = Path.Combine(BaseDir, "status.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static AgentSettings Load()
    {
        Directory.CreateDirectory(BaseDir);

        if (!File.Exists(ConfigFile))
        {
            var def = new AgentSettings
            {
                ClientId = GetLocalIp() ?? Environment.MachineName
            };

            Save(def);
            return def;
        }

        var json = File.ReadAllText(ConfigFile);
        var settings = JsonSerializer.Deserialize<AgentSettings>(json, JsonOptions) ?? new AgentSettings();

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            settings.ClientId = GetLocalIp() ?? Environment.MachineName;
        }

        if (settings.UploadIntervalSeconds < 1)
        {
            settings.UploadIntervalSeconds = 5;
        }

        settings.MonitoredApps = (settings.MonitoredApps ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return settings;
    }

    public static void Save(AgentSettings settings)
    {
        Directory.CreateDirectory(BaseDir);

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            settings.ClientId = GetLocalIp() ?? Environment.MachineName;
        }

        settings.MonitoredApps = (settings.MonitoredApps ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteJsonAtomic(ConfigFile, settings);
    }

    public static void SaveStatus(AgentStatus status)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            WriteJsonAtomic(StatusFile, status);
        }
        catch
        {
            // 状态文件写失败不影响服务运行
        }
    }

    public static AgentStatus? LoadStatus()
    {
        try
        {
            if (!File.Exists(StatusFile))
            {
                return null;
            }

            var json = File.ReadAllText(StatusFile);
            return JsonSerializer.Deserialize<AgentStatus>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteJsonAtomic<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, true);
    }

    private static string? GetLocalIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a =>
                    a.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(a.Address))
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
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
}
