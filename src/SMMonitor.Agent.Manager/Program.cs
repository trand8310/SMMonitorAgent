using Microsoft.Win32;

namespace SMMonitor.Agent.Manager;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var lowerArgs = (args ?? Array.Empty<string>())
            .Select(x => (x ?? "").Trim().ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (lowerArgs.Contains("--enable-autostart"))
        {
            SetAutoStart(true);
            return;
        }

        if (lowerArgs.Contains("--disable-autostart"))
        {
            SetAutoStart(false);
            return;
        }

        using var mutex = new Mutex(true, "Global\\SMMonitor.Agent.Manager.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        var startToTray = lowerArgs.Contains("--tray") || lowerArgs.Contains("/tray") || lowerArgs.Contains("--minimized");

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(startToTray));
    }

    private static void SetAutoStart(bool enabled)
    {
        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string runValueName = "SMMonitorAgentManager";

        using var key = Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true)
                       ?? Registry.CurrentUser.CreateSubKey(runKeyPath);

        if (key == null)
        {
            MessageBox.Show("无法访问开机启动注册表项。", "SMMonitorAgent Manager", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (enabled)
        {
            var exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            var value = $"\"{exePath}\" --tray";
            key.SetValue(runValueName, value, RegistryValueKind.String);
            MessageBox.Show("已设置为开机登录自动启动（托盘模式）。", "SMMonitorAgent Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            key.DeleteValue(runValueName, throwOnMissingValue: false);
            MessageBox.Show("已取消开机登录自动启动。", "SMMonitorAgent Manager", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
