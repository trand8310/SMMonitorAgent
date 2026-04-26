using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SMMonitor.Agent.Service;

public sealed class ResourceCollector
{
    private readonly DateTime _processStartTime = DateTime.Now;
    private readonly Dictionary<string, AppCpuSample> _appCpuSamples = new(StringComparer.OrdinalIgnoreCase);

    private ulong _lastIdle;
    private ulong _lastKernel;
    private ulong _lastUser;
    private bool _hasCpuSample;

    public MonitorSnapshot Collect(string version, IReadOnlyCollection<string>? monitoredApps = null)
    {
        var mem = NativeMethods.GetMemoryInfo();
        var disks = GetDisks();

        return new MonitorSnapshot
        {
            MachineName = Environment.MachineName,
            Os = RuntimeInformation.OSDescription,
            Version = version,
            Cpu = GetCpuUsagePercent(),
            MemoryUsedPercent = mem.UsedPercent,
            MemoryTotalMb = mem.TotalMb,
            MemoryAvailableMb = mem.AvailableMb,
            ProcessUptimeSeconds = (long)(DateTime.Now - _processStartTime).TotalSeconds,
            BootTime = DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64),
            Disks = disks,
            MonitoredApps = GetMonitoredAppStatuses(monitoredApps)
        };
    }

    private double GetCpuUsagePercent()
    {
        if (!NativeMethods.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            return 0;
        }

        var idle = NativeMethods.ToUInt64(idleTime);
        var kernel = NativeMethods.ToUInt64(kernelTime);
        var user = NativeMethods.ToUInt64(userTime);

        if (!_hasCpuSample)
        {
            _lastIdle = idle;
            _lastKernel = kernel;
            _lastUser = user;
            _hasCpuSample = true;
            return 0;
        }

        var idleDiff = idle - _lastIdle;
        var kernelDiff = kernel - _lastKernel;
        var userDiff = user - _lastUser;

        _lastIdle = idle;
        _lastKernel = kernel;
        _lastUser = user;

        var total = kernelDiff + userDiff;
        if (total == 0)
        {
            return 0;
        }

        var busy = total - idleDiff;
        return Math.Round(Math.Clamp(busy * 100.0 / total, 0, 100), 2);
    }

    private static List<DiskInfo> GetDisks()
    {
        var list = new List<DiskInfo>();

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                {
                    continue;
                }

                var total = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                var used = total - free;

                list.Add(new DiskInfo
                {
                    Name = drive.Name,
                    TotalGb = Math.Round(total / 1024d / 1024d / 1024d, 2),
                    FreeGb = Math.Round(free / 1024d / 1024d / 1024d, 2),
                    UsedPercent = total > 0 ? Math.Round(used * 100d / total, 2) : 0
                });
            }
            catch
            {
                // 某些盘符可能无权限或临时不可用，忽略即可
            }
        }

        return list;
    }

    private static List<MonitoredAppStatus> GetMonitoredAppStatuses(IReadOnlyCollection<string>? monitoredApps)
    {
        if (monitoredApps == null || monitoredApps.Count == 0)
        {
            return new List<MonitoredAppStatus>();
        }

        var list = new List<MonitoredAppStatus>();

        foreach (var app in monitoredApps
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processName = NormalizeProcessName(app);
            Process[] matches;

            try
            {
                matches = Process.GetProcessesByName(processName);
            }
            catch
            {
                matches = Array.Empty<Process>();
            }

            var cpuSeconds = 0d;
            var memoryBytes = 0L;
            var threadCount = 0;
            var startedAt = DateTime.MinValue;

            foreach (var p in matches)
            {
                try
                {
                    cpuSeconds += p.TotalProcessorTime.TotalSeconds;
                    memoryBytes += p.WorkingSet64;
                    threadCount += p.Threads.Count;

                    var st = p.StartTime;
                    if (startedAt == DateTime.MinValue || st < startedAt)
                    {
                        startedAt = st;
                    }
                }
                catch
                {
                    // 进程瞬时退出或权限不足时忽略
                }
                finally
                {
                    p.Dispose();
                }
            }

            list.Add(new MonitoredAppStatus
            {
                Name = processName,
                IsRunning = matches.Length > 0,
                ProcessCount = matches.Length,
                OldestStartTime = startedAt == DateTime.MinValue ? null : startedAt,
                TotalCpuSeconds = Math.Round(cpuSeconds, 2),
                CpuPercent = CalculateAppCpuPercent(processName, cpuSeconds),
                MemoryUsedMb = Math.Round(memoryBytes / 1024d / 1024d, 2),
                ThreadCount = threadCount
            });
        }

        return list;
    }

    private double CalculateAppCpuPercent(string processName, double totalCpuSeconds)
    {
        var now = DateTime.UtcNow;
        var coreCount = Math.Max(1, Environment.ProcessorCount);

        if (!_appCpuSamples.TryGetValue(processName, out var old))
        {
            _appCpuSamples[processName] = new AppCpuSample(totalCpuSeconds, now);
            return 0;
        }

        var elapsed = (now - old.Timestamp).TotalSeconds;
        if (elapsed <= 0)
        {
            return 0;
        }

        var delta = totalCpuSeconds - old.TotalCpuSeconds;
        _appCpuSamples[processName] = new AppCpuSample(totalCpuSeconds, now);

        if (delta <= 0)
        {
            return 0;
        }

        var usage = delta / elapsed / coreCount * 100d;
        return Math.Round(Math.Clamp(usage, 0, 100), 2);
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

public sealed class MonitorSnapshot
{
    public string MachineName { get; set; } = "";
    public string Os { get; set; } = "";
    public string Version { get; set; } = "";
    public double Cpu { get; set; }
    public double MemoryUsedPercent { get; set; }
    public ulong MemoryTotalMb { get; set; }
    public ulong MemoryAvailableMb { get; set; }
    public long ProcessUptimeSeconds { get; set; }
    public DateTime BootTime { get; set; }
    public List<DiskInfo> Disks { get; set; } = new();
    public List<MonitoredAppStatus> MonitoredApps { get; set; } = new();
}

public sealed class DiskInfo
{
    public string Name { get; set; } = "";
    public double TotalGb { get; set; }
    public double FreeGb { get; set; }
    public double UsedPercent { get; set; }
}

public sealed class MonitoredAppStatus
{
    public string Name { get; set; } = "";
    public bool IsRunning { get; set; }
    public int ProcessCount { get; set; }
    public DateTime? OldestStartTime { get; set; }
    public double TotalCpuSeconds { get; set; }
    public double CpuPercent { get; set; }
    public double MemoryUsedMb { get; set; }
    public int ThreadCount { get; set; }
}

file sealed record AppCpuSample(double TotalCpuSeconds, DateTime Timestamp);

public static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    public static ulong ToUInt64(FILETIME ft)
    {
        return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
    }

    public static MemoryInfo GetMemoryInfo()
    {
        var mem = new MEMORYSTATUSEX
        {
            dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>()
        };

        if (!GlobalMemoryStatusEx(ref mem))
        {
            return new MemoryInfo();
        }

        var totalMb = mem.ullTotalPhys / 1024 / 1024;
        var availMb = mem.ullAvailPhys / 1024 / 1024;
        var usedMb = totalMb > availMb ? totalMb - availMb : 0;

        return new MemoryInfo
        {
            TotalMb = totalMb,
            AvailableMb = availMb,
            UsedPercent = totalMb > 0 ? Math.Round(usedMb * 100d / totalMb, 2) : 0
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
}

public sealed class MemoryInfo
{
    public ulong TotalMb { get; set; }
    public ulong AvailableMb { get; set; }
    public double UsedPercent { get; set; }
}
