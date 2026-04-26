using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Diagnostics;

namespace SMMonitor.Common;

public static class AgentConfigStore
{
    private static readonly string LegacyBaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SMMonitorAgent");
    public static readonly string BaseDir = ResolveBaseDir();

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
        TryMigrateFromLegacyPath();

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

        settings.AppPipeName = NormalizePipeName(settings.AppPipeName);

        settings.MonitoredApps = (settings.MonitoredApps ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.MonitoredAppProfiles = NormalizeProfiles(settings.MonitoredAppProfiles);
        foreach (var profileName in settings.MonitoredAppProfiles.Select(x => x.Name))
        {
            if (!settings.MonitoredApps.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                settings.MonitoredApps.Add(profileName);
            }
        }

        return settings;
    }

    public static void Save(AgentSettings settings)
    {
        Directory.CreateDirectory(BaseDir);
        TryMigrateFromLegacyPath();

        if (string.IsNullOrWhiteSpace(settings.ClientId))
        {
            settings.ClientId = GetLocalIp() ?? Environment.MachineName;
        }

        settings.AppPipeName = NormalizePipeName(settings.AppPipeName);

        settings.MonitoredApps = (settings.MonitoredApps ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.MonitoredAppProfiles = NormalizeProfiles(settings.MonitoredAppProfiles);
        foreach (var profileName in settings.MonitoredAppProfiles.Select(x => x.Name))
        {
            if (!settings.MonitoredApps.Contains(profileName, StringComparer.OrdinalIgnoreCase))
            {
                settings.MonitoredApps.Add(profileName);
            }
        }

        WriteJsonAtomic(ConfigFile, settings);
    }

    public static void SaveStatus(AgentStatus status)
    {
        try
        {
            Directory.CreateDirectory(BaseDir);
            TryMigrateFromLegacyPath();
            WriteJsonAtomic(StatusFile, status);
        }
        catch
        {
            // 状态文件写失败不影响服务运行
        }
    }

    private static string ResolveBaseDir()
    {
        var envDir = Environment.GetEnvironmentVariable("SMMONITOR_PROFILE_DIR")?.Trim();
        if (!string.IsNullOrWhiteSpace(envDir))
        {
            return envDir;
        }

        var currentBase = AppContext.BaseDirectory;
        var currentProcessName = Process.GetCurrentProcess().ProcessName;
        if (currentProcessName.Contains("SMMonitor.Agent.Service", StringComparison.OrdinalIgnoreCase) ||
            File.Exists(Path.Combine(currentBase, "SMMonitor.Agent.Service.exe")))
        {
            return Path.Combine(currentBase, "profile");
        }

        // manager 发布目录通常为 publish/manager，服务目录为 publish/service
        var siblingServiceDir = Path.GetFullPath(Path.Combine(currentBase, "..", "service"));
        if (Directory.Exists(siblingServiceDir) &&
            File.Exists(Path.Combine(siblingServiceDir, "SMMonitor.Agent.Service.exe")))
        {
            return Path.Combine(siblingServiceDir, "profile");
        }

        return LegacyBaseDir;
    }

    private static void TryMigrateFromLegacyPath()
    {
        if (string.Equals(BaseDir, LegacyBaseDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var legacyConfig = Path.Combine(LegacyBaseDir, "agentsettings.json");
            var legacyStatus = Path.Combine(LegacyBaseDir, "status.json");

            if (!File.Exists(ConfigFile) && File.Exists(legacyConfig))
            {
                Directory.CreateDirectory(BaseDir);
                File.Copy(legacyConfig, ConfigFile, overwrite: false);
            }

            if (!File.Exists(StatusFile) && File.Exists(legacyStatus))
            {
                Directory.CreateDirectory(BaseDir);
                File.Copy(legacyStatus, StatusFile, overwrite: false);
            }
        }
        catch
        {
            // 迁移失败不影响正常读写
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

    private static List<MonitoredAppProfile> NormalizeProfiles(List<MonitoredAppProfile>? profiles)
    {
        return (profiles ?? new List<MonitoredAppProfile>())
            .Where(x => !string.IsNullOrWhiteSpace(x.Name) || !string.IsNullOrWhiteSpace(x.FilePath))
            .Select(x =>
            {
                var filePath = x.FilePath?.Trim() ?? "";
                var name = x.Name?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(filePath))
                {
                    name = Path.GetFileNameWithoutExtension(filePath);
                }

                return new MonitoredAppProfile
                {
                    Name = NormalizeProcessName(name),
                    FilePath = filePath,
                    Arguments = x.Arguments?.Trim() ?? ""
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Name))
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static string NormalizePipeName(string? raw)
    {
        return AgentSettings.FixedPipeName;
    }
}
