using System.Diagnostics;
using System.IO.Pipes;
using System.ServiceProcess;
using System.Text.Json;
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
    private readonly ComboBox _cmbLogCategory = new();
    private readonly TextBox _txtLogKeyword = new();
    private readonly ListBox _lstLogs = new();
    private readonly List<ManagerPipeLog> _logs = new();
    private readonly object _logLock = new();
    private readonly NotifyIcon _trayIcon = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private bool _exitRequested;

    private readonly Label _lblServiceStatus = new();
    private readonly Label _lblWsStatus = new();
    private readonly Label _lblLastUpload = new();
    private readonly Label _lblCpu = new();
    private readonly Label _lblMemory = new();
    private readonly Label _lblDisk = new();
    private readonly Label _lblError = new();

    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly CancellationTokenSource _loopCts = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public MainForm(bool startToTray = false)
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

        _ = Task.Run(() => ManagerCommandServer.RunAsync(AddLog, _loopCts.Token));
        InitTray();
        FormClosing += MainForm_FormClosing;
        Resize += MainForm_Resize;

        if (startToTray)
        {
            Shown += (_, _) =>
            {
                WindowState = FormWindowState.Minimized;
                Hide();
            };
        }
    }

    private void InitTray()
    {
        _trayMenu.Items.Add("打开管理器", null, (_, _) => RestoreFromTray());
        _trayMenu.Items.Add("退出", null, (_, _) =>
        {
            _exitRequested = true;
            Close();
        });

        _trayIcon.Text = "SMMonitorAgent Manager";
        _trayIcon.Icon = SystemIcons.Application;
        _trayIcon.ContextMenuStrip = _trayMenu;
        _trayIcon.Visible = true;
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
    }

    private void MainForm_Resize(object? sender, EventArgs e)
    {
        if (WindowState == FormWindowState.Minimized)
        {
            Hide();
        }
    }

    private void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_exitRequested && e.CloseReason != CloseReason.WindowsShutDown && e.CloseReason != CloseReason.TaskManagerClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _loopCts.Cancel();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    private void BuildUi()
    {
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill
        };
        Controls.Add(tabs);

        var tabConn = new TabPage("连接配置");
        var tabMonitor = new TabPage("监控配置");
        var tabService = new TabPage("服务控制");
        var tabStatus = new TabPage("运行状态");
        var tabLogs = new TabPage("日志");
        tabs.TabPages.AddRange([tabConn, tabMonitor, tabService, tabStatus, tabLogs]);

        var conn = CreatePageLayout(tabConn);
        AddRow(conn, "WS服务器地址", _txtServerUrl);
        AddRow(conn, "Token", _txtToken);
        AddRow(conn, "客户端ID", _txtClientId);
        AddRow(conn, "版本号", _txtVersion);
        _numInterval.Minimum = 1;
        _numInterval.Maximum = 3600;
        _numInterval.Value = 5;
        AddRow(conn, "上报间隔秒", _numInterval);
        _chkEnableUpload.Text = "启用资源上报";
        _chkEnableReboot.Text = "允许服务端远程重启本机";
        AddRow(conn, "上报开关", _chkEnableUpload);
        AddRow(conn, "远程重启", _chkEnableReboot);

        var monitor = CreatePageLayout(tabMonitor);
        InitPercentBox(_numCpu, 95);
        InitPercentBox(_numMemory, 90);
        InitPercentBox(_numDisk, 90);
        AddRow(monitor, "CPU阈值 %", _numCpu);
        AddRow(monitor, "内存阈值 %", _numMemory);
        AddRow(monitor, "磁盘阈值 %", _numDisk);
        _txtMonitoredApps.Multiline = true;
        _txtMonitoredApps.ScrollBars = ScrollBars.Vertical;
        AddRow(monitor, "监控应用(每行: 名称|完整EXE路径|参数；路径可留空)", _txtMonitoredApps, 180);
        _chkAutoCaptureOnFailure.Text = "应用异常时自动截图并随告警上报";
        AddRow(monitor, "异常截图", _chkAutoCaptureOnFailure);
        _chkEnablePipeForward.Text = "启用应用命名管道消息转发";
        AddRow(monitor, "管道转发", _chkEnablePipeForward);
        _txtPipeName.ReadOnly = true;
        _txtPipeName.Text = AgentSettings.FixedPipeName;
        AddRow(monitor, "固定管道名", _txtPipeName);

        var svc = CreatePageLayout(tabService);
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
        btnPanel.Controls.AddRange([btnSave, btnRestart, btnStart, btnStop, btnOpenDir, btnRefresh]);
        AddRow(svc, "服务控制", btnPanel, 56);

        var status = CreatePageLayout(tabStatus);
        AddStatusRow(status, "服务状态", _lblServiceStatus);
        AddStatusRow(status, "WS连接", _lblWsStatus);
        AddStatusRow(status, "最后上报", _lblLastUpload);
        AddStatusRow(status, "CPU", _lblCpu);
        AddStatusRow(status, "内存", _lblMemory);
        AddStatusRow(status, "磁盘最高使用", _lblDisk);
        AddStatusRow(status, "错误", _lblError, 64);

        BuildLogPage(tabLogs);
    }

    private static TableLayoutPanel CreatePageLayout(TabPage page)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18),
            AutoScroll = true
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 165));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.Controls.Add(layout);
        return layout;
    }

    private void BuildLogPage(TabPage page)
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        page.Controls.Add(root);

        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _cmbLogCategory.Width = 180;
        _cmbLogCategory.DropDownStyle = ComboBoxStyle.DropDownList;
        _cmbLogCategory.Items.AddRange(new object[] { "全部", "Service", "App" });
        _cmbLogCategory.SelectedIndex = 0;
        _cmbLogCategory.SelectedIndexChanged += (_, _) => RefreshLogList();
        _txtLogKeyword.Width = 220;
        _txtLogKeyword.PlaceholderText = "关键字过滤";
        _txtLogKeyword.TextChanged += (_, _) => RefreshLogList();
        var btnClear = MakeButton("清空日志", 105);
        btnClear.Click += (_, _) =>
        {
            lock (_logLock) _logs.Clear();
            RefreshLogList();
        };
        toolbar.Controls.AddRange(new Control[] { new Label { Text = "类别", Width = 36, TextAlign = ContentAlignment.MiddleLeft }, _cmbLogCategory, _txtLogKeyword, btnClear });
        root.Controls.Add(toolbar, 0, 0);

        _lstLogs.Dock = DockStyle.Fill;
        _lstLogs.HorizontalScrollbar = true;
        root.Controls.Add(_lstLogs, 0, 1);
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
        _txtPipeName.Text = AgentSettings.FixedPipeName;
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
            AppPipeName = AgentSettings.FixedPipeName
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

    private void AddLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var item = new ManagerPipeLog
        {
            Timestamp = DateTime.Now,
            Category = "App",
            Source = "",
            Message = line
        };

        try
        {
            var parsed = JsonSerializer.Deserialize<ManagerPipeLog>(line, JsonOptions);
            if (parsed != null)
            {
                item = parsed;
            }
        }
        catch
        {
        }

        item.Message = item.Message?.Trim() ?? "";
        if (item.Message.Length == 0)
        {
            return;
        }

        if (item.Source.Length == 0 && item.Message.StartsWith("[ManagerCmd]", StringComparison.OrdinalIgnoreCase))
        {
            item.Source = "ManagerCmd";
            item.Category = "Service";
        }

        if (string.Equals(item.Source, "ConfigSync", StringComparison.OrdinalIgnoreCase))
        {
            TryApplyRemoteConfigSync(item.Message);
        }

        lock (_logLock)
        {
            _logs.Insert(0, item);
            if (_logs.Count > 2000)
            {
                _logs.RemoveRange(2000, _logs.Count - 2000);
            }
        }

        RefreshLogList();
    }

    private void TryApplyRemoteConfigSync(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ManagerConfigSyncPayload>(message, JsonOptions);
            if (payload == null)
            {
                return;
            }

            BeginInvoke(() =>
            {
                _txtMonitoredApps.Text = string.Join(Environment.NewLine, BuildMonitoredAppLines(new AgentSettings
                {
                    MonitoredApps = payload.MonitoredApps ?? new List<string>(),
                    MonitoredAppProfiles = payload.MonitoredAppProfiles ?? new List<MonitoredAppProfile>()
                }));
                _chkAutoCaptureOnFailure.Checked = payload.AutoCaptureScreenshotOnAppFailure;
                _chkEnablePipeForward.Checked = payload.EnablePipeForward;
            });
        }
        catch
        {
            // ignore malformed payload
        }
    }

    private void RefreshLogList()
    {
        if (IsHandleCreated == false) return;

        BeginInvoke(() =>
        {
            var category = _cmbLogCategory.SelectedItem?.ToString() ?? "全部";
            var keyword = (_txtLogKeyword.Text ?? "").Trim();
            List<ManagerPipeLog> items;
            lock (_logLock)
            {
                items = _logs.ToList();
            }

            var filtered = items
                .Where(x => category == "全部" || string.Equals(x.Category, category, StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(x.Message))
                .Where(x => keyword.Length == 0 || (x.Message?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false))
                .Take(500)
                .Select(x => $"[{x.Timestamp:HH:mm:ss}] [{x.Category}] [{x.Source}] {x.Message}")
                .ToArray();

            _lstLogs.BeginUpdate();
            _lstLogs.Items.Clear();
            _lstLogs.Items.AddRange(filtered);
            _lstLogs.EndUpdate();
        });
    }
}

public sealed class ManagerPipeLog
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string Category { get; set; } = "App";
    public string Source { get; set; } = "";
    public string Message { get; set; } = "";
}

public sealed class ManagerConfigSyncPayload
{
    public List<string>? MonitoredApps { get; set; }
    public List<MonitoredAppProfile>? MonitoredAppProfiles { get; set; }
    public bool AutoCaptureScreenshotOnAppFailure { get; set; }
    public bool EnablePipeForward { get; set; }
}
