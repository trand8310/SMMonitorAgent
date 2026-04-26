using System.Diagnostics;
using System.ServiceProcess;
using SMMonitor.Common;

namespace SMMonitor.Agent.Manager;

public sealed class MainForm : Form
{
    private const string ServiceName = "SMMonitorAgent";

    private readonly TextBox _txtServerUrl = new();
    private readonly TextBox _txtToken = new();
    private readonly TextBox _txtClientId = new();
    private readonly TextBox _txtVersion = new();
    private readonly NumericUpDown _numInterval = new();
    private readonly NumericUpDown _numCpu = new();
    private readonly NumericUpDown _numMemory = new();
    private readonly NumericUpDown _numDisk = new();
    private readonly CheckBox _chkEnableUpload = new();
    private readonly CheckBox _chkEnableReboot = new();
    private readonly CheckBox _chkAutoCaptureOnFailure = new();
    private readonly CheckBox _chkEnablePipeForward = new();
    private readonly TextBox _txtMonitoredApps = new();
    private readonly TextBox _txtPipeName = new();

    private readonly Label _lblServiceStatus = new();
    private readonly Label _lblWsStatus = new();
    private readonly Label _lblLastUpload = new();
    private readonly Label _lblCpu = new();
    private readonly Label _lblMemory = new();
    private readonly Label _lblDisk = new();
    private readonly Label _lblError = new();

    private readonly System.Windows.Forms.Timer _timer = new();

    public MainForm()
    {
        Text = "SMMonitorAgent 管理工具";
        Width = 960;
        Height = 680;
        MinimumSize = new Size(760, 560);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);

        BuildUi();
        LoadConfig();
        RefreshStatus();

        _timer.Interval = 3000;
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18),
            AutoScroll = true
        };

        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(root);

        AddTitle(root, "连接配置");
        AddRow(root, "WS服务器地址", _txtServerUrl);
        AddRow(root, "Token", _txtToken);
        AddRow(root, "客户端ID", _txtClientId);
        AddRow(root, "版本号", _txtVersion);

        _numInterval.Minimum = 1;
        _numInterval.Maximum = 3600;
        _numInterval.Value = 5;
        AddRow(root, "上报间隔秒", _numInterval);

        _chkEnableUpload.Text = "启用资源上报";
        _chkEnableReboot.Text = "允许服务端远程重启本机";
        AddRow(root, "上报开关", _chkEnableUpload);
        AddRow(root, "远程重启", _chkEnableReboot);

        AddTitle(root, "告警阈值");

        InitPercentBox(_numCpu, 95);
        InitPercentBox(_numMemory, 90);
        InitPercentBox(_numDisk, 90);

        AddRow(root, "CPU阈值 %", _numCpu);
        AddRow(root, "内存阈值 %", _numMemory);
        AddRow(root, "磁盘阈值 %", _numDisk);

        _txtMonitoredApps.Multiline = true;
        _txtMonitoredApps.ScrollBars = ScrollBars.Vertical;
        AddRow(root, "监控应用(每行: 名称|路径|参数)", _txtMonitoredApps, 120);

        _chkAutoCaptureOnFailure.Text = "应用异常时自动截图并随告警上报";
        AddRow(root, "异常截图", _chkAutoCaptureOnFailure);

        _chkEnablePipeForward.Text = "启用应用命名管道消息转发";
        AddRow(root, "管道转发", _chkEnablePipeForward);

        var pipePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoSize = true
        };
        _txtPipeName.Width = 520;
        _txtPipeName.PlaceholderText = "例如：SMMONITOR_PIPE_7f8e5fd8d6f24f7fabf4b1291bc03a3d";
        var btnGenPipe = MakeButton("生成GUID管道标识", 160);
        btnGenPipe.Click += (_, _) =>
        {
            _txtPipeName.Text = "SMMONITOR_PIPE_" + Guid.NewGuid().ToString("N");
        };
        pipePanel.Controls.Add(_txtPipeName);
        pipePanel.Controls.Add(btnGenPipe);
        AddRow(root, "管道名称", pipePanel, 46);

        AddTitle(root, "操作");

        var btnPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true
        };

        var btnSave = MakeButton("保存配置", 105);
        var btnRestart = MakeButton("重启服务", 105);
        var btnStart = MakeButton("启动服务", 105);
        var btnStop = MakeButton("停止服务", 105);
        var btnOpenDir = MakeButton("打开配置目录", 125);
        var btnRefresh = MakeButton("刷新状态", 105);

        btnSave.Click += (_, _) => SaveConfig();
        btnRestart.Click += (_, _) => RestartService();
        btnStart.Click += (_, _) => StartService();
        btnStop.Click += (_, _) => StopService();
        btnOpenDir.Click += (_, _) => OpenConfigDir();
        btnRefresh.Click += (_, _) => RefreshStatus();

        btnPanel.Controls.AddRange(new Control[]
        {
            btnSave, btnRestart, btnStart, btnStop, btnOpenDir, btnRefresh
        });

        AddRow(root, "服务控制", btnPanel, 56);

        AddTitle(root, "运行状态");
        AddStatusRow(root, "服务状态", _lblServiceStatus);
        AddStatusRow(root, "WS连接", _lblWsStatus);
        AddStatusRow(root, "最后上报", _lblLastUpload);
        AddStatusRow(root, "CPU", _lblCpu);
        AddStatusRow(root, "内存", _lblMemory);
        AddStatusRow(root, "磁盘最高使用", _lblDisk);
        AddStatusRow(root, "错误", _lblError, 64);

        var tip = new Label
        {
            Text = "说明：Windows服务不能直接显示界面，本工具通过配置文件控制后台服务。修改 WS 地址、Token、客户端ID 后建议重启服务。",
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            AutoSize = true
        };
        AddRow(root, "提示", tip, 50);
    }

    private static void InitPercentBox(NumericUpDown box, int value)
    {
        box.Minimum = 1;
        box.Maximum = 100;
        box.Value = value;
    }

    private static Button MakeButton(string text, int width)
    {
        return new Button
        {
            Text = text,
            Width = width,
            Height = 32,
            Margin = new Padding(0, 4, 8, 4)
        };
    }

    private static void AddTitle(TableLayoutPanel root, string text)
    {
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        var label = new Label
        {
            Text = text,
            Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        root.Controls.Add(label, 0, row);
        root.SetColumnSpan(label, 2);
    }

    private static void AddRow(TableLayoutPanel root, string label, Control input, int height = 40)
    {
        var row = root.RowCount++;
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, height));

        root.Controls.Add(new Label
        {
            Text = label,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, row);

        input.Dock = DockStyle.Fill;
        root.Controls.Add(input, 1, row);
    }

    private static void AddStatusRow(TableLayoutPanel root, string label, Label value, int height = 34)
    {
        value.Dock = DockStyle.Fill;
        value.TextAlign = ContentAlignment.MiddleLeft;
        AddRow(root, label, value, height);
    }

    private void LoadConfig()
    {
        var cfg = AgentConfigStore.Load();

        _txtServerUrl.Text = cfg.ServerUrl;
        _txtToken.Text = cfg.Token;
        _txtClientId.Text = cfg.ClientId;
        _txtVersion.Text = cfg.Version;
        _numInterval.Value = Math.Clamp(cfg.UploadIntervalSeconds, 1, 3600);
        _chkEnableUpload.Checked = cfg.EnableUpload;
        _chkEnableReboot.Checked = cfg.EnableRemoteReboot;
        _numCpu.Value = Math.Clamp(cfg.CpuAlertPercent, 1, 100);
        _numMemory.Value = Math.Clamp(cfg.MemoryAlertPercent, 1, 100);
        _numDisk.Value = Math.Clamp(cfg.DiskAlertPercent, 1, 100);
        _txtMonitoredApps.Text = string.Join(Environment.NewLine, BuildMonitoredAppLines(cfg));
        _chkAutoCaptureOnFailure.Checked = cfg.AutoCaptureScreenshotOnAppFailure;
        _chkEnablePipeForward.Checked = cfg.EnablePipeForward;
        _txtPipeName.Text = cfg.AppPipeName;
    }

    private void SaveConfig()
    {
        var cfg = new AgentSettings
        {
            ServerUrl = _txtServerUrl.Text.Trim(),
            Token = _txtToken.Text.Trim(),
            ClientId = _txtClientId.Text.Trim(),
            Version = _txtVersion.Text.Trim(),
            UploadIntervalSeconds = (int)_numInterval.Value,
            EnableUpload = _chkEnableUpload.Checked,
            EnableRemoteReboot = _chkEnableReboot.Checked,
            CpuAlertPercent = (int)_numCpu.Value,
            MemoryAlertPercent = (int)_numMemory.Value,
            DiskAlertPercent = (int)_numDisk.Value,
            MonitoredApps = ParseMonitoredNames(_txtMonitoredApps.Text),
            MonitoredAppProfiles = ParseMonitoredProfiles(_txtMonitoredApps.Text),
            AutoCaptureScreenshotOnAppFailure = _chkAutoCaptureOnFailure.Checked,
            EnablePipeForward = _chkEnablePipeForward.Checked,
            AppPipeName = _txtPipeName.Text.Trim()
        };

        AgentConfigStore.Save(cfg);

        MessageBox.Show(
            "配置已保存。\r\n如果修改了 WS 地址、Token、客户端ID，建议点击【重启服务】生效。",
            "提示",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static List<string> BuildMonitoredAppLines(AgentSettings cfg)
    {
        if (cfg.MonitoredAppProfiles is { Count: > 0 })
        {
            return cfg.MonitoredAppProfiles
                .Select(x => $"{x.Name}|{x.FilePath}|{x.Arguments}".TrimEnd('|'))
                .ToList();
        }

        return cfg.MonitoredApps?.ToList() ?? new List<string>();
    }

    private static List<string> ParseMonitoredNames(string raw)
    {
        return ParseMonitoredProfiles(raw)
            .Select(x => x.Name)
            .Concat((raw ?? "")
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(x => !x.Contains('|'))
                .Select(NormalizeProcessName))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<MonitoredAppProfile> ParseMonitoredProfiles(string raw)
    {
        var list = new List<MonitoredAppProfile>();
        foreach (var line in (raw ?? "")
                     .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 2)
            {
                continue;
            }

            var name = NormalizeProcessName(parts[0]);
            var path = parts[1].Trim();
            var args = parts.Length >= 3 ? parts[2].Trim() : "";

            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path))
            {
                name = NormalizeProcessName(Path.GetFileNameWithoutExtension(path));
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            list.Add(new MonitoredAppProfile
            {
                Name = name,
                FilePath = path,
                Arguments = args
            });
        }

        return list
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .ToList();
    }

    private static string NormalizeProcessName(string raw)
    {
        var value = (raw ?? "").Trim();
        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }
        return value;
    }

    private void RefreshStatus()
    {
        _lblServiceStatus.Text = GetServiceStatusText();

        var status = AgentConfigStore.LoadStatus();
        if (status == null)
        {
            _lblWsStatus.Text = "-";
            _lblLastUpload.Text = "-";
            _lblCpu.Text = "-";
            _lblMemory.Text = "-";
            _lblDisk.Text = "-";
            _lblError.Text = "暂无状态文件";
            return;
        }

        _lblWsStatus.Text = status.WsConnected ? "已连接" : "未连接";
        _lblWsStatus.ForeColor = status.WsConnected ? Color.ForestGreen : Color.Firebrick;
        _lblLastUpload.Text = status.LastUploadTime == default ? "-" : status.LastUploadTime.ToString("yyyy-MM-dd HH:mm:ss");
        _lblCpu.Text = $"{status.Cpu:F1}%";
        _lblMemory.Text = $"{status.MemoryUsedPercent:F1}%";
        _lblDisk.Text = $"{status.DiskMaxUsedPercent:F1}%";
        _lblError.Text = string.IsNullOrWhiteSpace(status.LastError) ? "正常" : status.LastError;
        _lblError.ForeColor = string.IsNullOrWhiteSpace(status.LastError) ? Color.ForestGreen : Color.Firebrick;
    }

    private static string GetServiceStatusText()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => "运行中",
                ServiceControllerStatus.Stopped => "已停止",
                ServiceControllerStatus.StartPending => "启动中",
                ServiceControllerStatus.StopPending => "停止中",
                ServiceControllerStatus.Paused => "已暂停",
                _ => sc.Status.ToString()
            };
        }
        catch
        {
            return "未安装";
        }
    }

    private static void OpenConfigDir()
    {
        Directory.CreateDirectory(AgentConfigStore.BaseDir);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = AgentConfigStore.BaseDir,
            UseShellExecute = true
        });
    }

    private void StartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);

            if (sc.Status == ServiceControllerStatus.Running)
            {
                MessageBox.Show("服务已经在运行。", "提示");
                return;
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动服务失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void StopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);

            if (sc.Status == ServiceControllerStatus.Stopped)
            {
                MessageBox.Show("服务已经停止。", "提示");
                return;
            }

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            RefreshStatus();
        }
        catch (Exception ex)
        {
            MessageBox.Show("停止服务失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void RestartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);

            if (sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(25));
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(25));
            RefreshStatus();
            MessageBox.Show("服务已重启。", "提示");
        }
        catch (Exception ex)
        {
            MessageBox.Show("重启服务失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
