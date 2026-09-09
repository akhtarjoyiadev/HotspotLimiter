using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace HotspotLimiter;

internal static class Program
{
    private static Mutex? singleInstanceMutex;

    [STAThread]
    static void Main(string[] args)
    {
        Log.Init();
        Log.Write("HotspotLimiter starting. Args: " + string.Join(" ", args));

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("ThreadException: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("UnhandledException: " + e.ExceptionObject);

        try
        {
            singleInstanceMutex = new Mutex(true, "HotspotLimiter_SingleInstance_App_Mutex", out bool isNewInstance);
            if (!isNewInstance)
            {
                Log.Write("Another instance is already running; exiting.");
                return;
            }

            ExtractResource("HotspotLimiter.WinDivert.dll", "WinDivert.dll");
            ExtractResource("HotspotLimiter.WinDivert64.sys", "WinDivert64.sys");

            ApplicationConfiguration.Initialize();
            bool startMinimized = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase) || a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

            var mainForm = new MainForm(startMinimized);
            Application.Run(mainForm);

            singleInstanceMutex.ReleaseMutex();
        }
        catch (Exception ex)
        {
            Log.Write("Fatal in Main: " + ex);
        }
    }

    private static void ExtractResource(string resourceName, string fileName)
    {
        try
        {
            var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (!File.Exists(path))
            {
                using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
                if (stream != null)
                {
                    using var file = File.Create(path);
                    stream.CopyTo(file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("Error extracting " + fileName + ": " + ex);
        }
    }
}

public sealed class MainForm : Form
{
    readonly ComboBox adapter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 310 };
    readonly NumericUpDown limit = new() { Minimum = 0.5m, Maximum = 100000, Value = 4.0m, DecimalPlaces = 1, Increment = 0.5m, Width = 80 };
    readonly NumericUpDown dailyLimit = new() { Minimum = 0.0m, Maximum = 1000.0m, Value = 1.0m, DecimalPlaces = 2, Increment = 0.25m, Width = 80 };
    readonly Button start = new() { Text = "▶ Start limiter", Width = 110, Height = 28 };
    readonly Button stop = new() { Text = "⏹ Stop", Width = 80, Height = 28, Enabled = false };
    readonly Button toggleHotspot = new() { Text = "📶 Toggle Hotspot", Width = 130, Height = 28 };
    readonly Button resetStats = new() { Text = "↺ Reset Stats", Width = 95, Height = 28 };

    readonly Label status = new() { AutoSize = true, Text = "Starting...", Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
    readonly Label usage = new() { AutoSize = true, Text = "Total Forwarded: 0.0 MB | Devices: 0", Font = new Font("Segoe UI", 9f) };
    readonly Label hotspotStatusLabel = new() { AutoSize = true, Text = "Hotspot: Checking...", Font = new Font("Segoe UI", 9f) };

    readonly CheckBox autoStartHotspot = new() { Text = "Auto-start Mobile Hotspot if Off", AutoSize = true, Checked = true };
    readonly CheckBox autoStartLimiter = new() { Text = "Auto-start limiter", AutoSize = true, Checked = false };
    readonly CheckBox startup = new() { Text = "Start with Windows (10s delay, silent)", AutoSize = true };
    readonly CheckBox minimizeToTray = new() { Text = "Minimize to tray on [X]", AutoSize = true, Checked = true };

    readonly ListView deviceList = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        GridLines = true,
        MultiSelect = false
    };

    readonly NotifyIcon trayIcon = new();
    readonly ContextMenuStrip trayMenu = new();

    Limiter? limiter;
    uint activeVirtualIdx = 0;
    uint? activeUplinkIdx = null;
    System.Windows.Forms.Timer? statsTimer;
    System.Windows.Forms.Timer? autoDetectTimer;
    bool isExiting = false;
    bool limiterManuallyStopped = false;
    bool hotspotWasOn = false;
    bool autoDetectRunning = false;
    readonly bool startedMinimized;

    public MainForm(bool startMinimized)
    {
        this.startedMinimized = startMinimized;
        var savedSettings = SettingsStore.Load();
        limit.Value = ClampDecimal(savedSettings.LimitMbps, limit.Minimum, limit.Maximum);
        dailyLimit.Value = ClampDecimal(savedSettings.DailyLimitGb, dailyLimit.Minimum, dailyLimit.Maximum);
        autoStartLimiter.Checked = savedSettings.AutoStartLimiter;
        Text = "Hotspot Limiter & Wi-Fi Tracker (4.0 Mbps)";
        Width = 750; Height = 560;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(650, 420);

        InitTrayIcon();
        InitDeviceList();

        // Layout
        var mainContainer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        mainContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 165));
        mainContainer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        mainContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));

        var topPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 5 };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));

        topPanel.Controls.Add(new Label { Text = "Hotspot Adapter:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        topPanel.Controls.Add(adapter, 1, 0);
        topPanel.Controls.Add(new Label { Text = "Total Cap (Mbps):", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        topPanel.Controls.Add(limit, 3, 0);

        topPanel.Controls.Add(new Label { Text = "Daily Limit / Device (GB):", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        topPanel.Controls.Add(dailyLimit, 1, 4);

        var buttonFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttonFlow.Controls.Add(start);
        buttonFlow.Controls.Add(stop);
        buttonFlow.Controls.Add(toggleHotspot);
        buttonFlow.Controls.Add(resetStats);
        topPanel.SetColumnSpan(buttonFlow, 2);
        topPanel.Controls.Add(buttonFlow, 1, 1);

        var checkFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight };
        checkFlow.Controls.Add(autoStartHotspot);
        checkFlow.Controls.Add(autoStartLimiter);
        checkFlow.Controls.Add(startup);
        checkFlow.Controls.Add(minimizeToTray);
        topPanel.SetColumnSpan(checkFlow, 4);
        topPanel.Controls.Add(checkFlow, 0, 2);

        topPanel.Controls.Add(new Label { Text = "Limiter Status:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);

        var statusFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        statusFlow.Controls.Add(status);
        statusFlow.Controls.Add(new Label { Text = "  |  ", AutoSize = true });
        statusFlow.Controls.Add(hotspotStatusLabel);
        topPanel.SetColumnSpan(statusFlow, 3);
        topPanel.Controls.Add(statusFlow, 1, 3);

        mainContainer.Controls.Add(topPanel, 0, 0);

        var listGroup = new GroupBox { Text = "Connected Devices & Live Bandwidth Usage", Dock = DockStyle.Fill, Padding = new Padding(8) };
        listGroup.Controls.Add(deviceList);
        mainContainer.Controls.Add(listGroup, 0, 1);

        var bottomPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight };
        bottomPanel.Controls.Add(usage);
        mainContainer.Controls.Add(bottomPanel, 0, 2);

        Controls.Add(mainContainer);

        LoadAdapters();
        start.Click += (_, _) => StartLimiter();
        stop.Click += (_, _) => StopLimiter();
        toggleHotspot.Click += async (_, _) => await ToggleHotspotAsync();
        resetStats.Click += (_, _) => ResetAllStats();
        startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
        startup.Checked = StartupManager.IsEnabled();
        limit.ValueChanged += (_, _) => SaveSettings();
        dailyLimit.ValueChanged += (_, _) => SaveSettings();
        autoStartLimiter.CheckedChanged += (_, _) => SaveSettings();

        statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        statsTimer.Tick += (_, _) => UpdateStats();
        statsTimer.Start();

        // Auto-detect and monitor Hotspot state (3s loop)
        autoDetectTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        autoDetectTimer.Tick += async (_, _) =>
        {
            if (autoDetectRunning) return;
            autoDetectRunning = true;
            try
            {
                bool isHotspotOn = await HotspotController.IsHotspotOnAsync();
                hotspotStatusLabel.Text = isHotspotOn ? "Hotspot: On (Wi-Fi)" : "Hotspot: Off";
                hotspotStatusLabel.ForeColor = isHotspotOn ? Color.DarkGreen : Color.DarkOrange;

                if (autoStartHotspot.Checked && !isHotspotOn)
                {
                    Log.Write("Hotspot is off - attempting to start it.");
                    await HotspotController.StartHotspotAsync();
                    LoadAdapters();
                    isHotspotOn = await HotspotController.IsHotspotOnAsync();
                }

                if (isHotspotOn)
                {
                    if (!hotspotWasOn)
                    {
                        // A newly started hotspot is a new opportunity for automatic startup.
                        limiterManuallyStopped = false;
                    }
                    hotspotWasOn = true;

                    var virtualAdapter = FindHotspotAdapter();
                    if (virtualAdapter is not null)
                    {
                        var uplink = FindUplinkAdapter(virtualAdapter);
                        uint vIdx = GetInterfaceIndex(virtualAdapter);
                        uint? uIdx = uplink is not null ? GetInterfaceIndex(uplink) : null;

                        bool adapterChanged = limiter is not null &&
                            (activeVirtualIdx != vIdx || activeUplinkIdx != uIdx);
                        bool shouldStartAutomatically = limiter is null && autoStartLimiter.Checked;
                        if (!limiterManuallyStopped && (shouldStartAutomatically || adapterChanged))
                        {
                            Log.Write($"Hotspot detected: virtual={virtualAdapter.Name} (ifIdx {vIdx}), uplink={(uplink?.Name ?? "none")} (ifIdx {uIdx?.ToString() ?? "-"}) - (re)binding limiter.");
                            LoadAdapters();
                            StartLimiter(virtualAdapter, uplink);
                        }
                        else if (limiter is null)
                        {
                            status.Text = limiterManuallyStopped
                                ? "Stopped (manual)"
                                : "Hotspot ready - click Start limiter";
                            status.ForeColor = Color.DarkOrange;
                        }
                    }
                    else
                    {
                        Log.Write("Hotspot reported On but no hotspot virtual adapter found; waiting.");
                        status.Text = "Waiting for hotspot adapter...";
                        status.ForeColor = Color.DarkOrange;
                    }
                }
                else
                {
                    hotspotWasOn = false;
                    limiterManuallyStopped = false;
                    if (limiter is not null)
                    {
                        Log.Write("Hotspot turned off; stopping limiter.");
                        StopLimiter(false);
                    }
                    else
                    {
                        status.Text = autoStartHotspot.Checked
                            ? "Waiting for Wi-Fi Hotspot..."
                            : "Hotspot is off";
                        status.ForeColor = Color.DarkOrange;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("Auto-detect error: " + ex);
            }
            finally
            {
                autoDetectRunning = false;
            }
        };
        autoDetectTimer.Start();

        // Startup routine
        Task.Run(async () =>
        {
            try
            {
                if (startMinimized)
                {
                    await Task.Delay(5000);
                }

                for (int attempt = 0; attempt < 3 && !await HotspotController.IsHotspotOnAsync(); attempt++)
                {
                    if (autoStartHotspot.Checked)
                    {
                        Log.Write($"Startup routine: starting hotspot (attempt {attempt + 1}/3).");
                        await HotspotController.StartHotspotAsync();
                    }
                    for (int i = 0; i < 15 && !await HotspotController.IsHotspotOnAsync(); i++)
                    {
                        await Task.Delay(1000);
                    }
                }

                Invoke(new Action(() =>
                {
                    LoadAdapters();
                    var virtualAdapter = FindHotspotAdapter();
                    if (virtualAdapter is not null && autoStartLimiter.Checked)
                    {
                        StartLimiter(virtualAdapter, FindUplinkAdapter(virtualAdapter));
                    }
                    else if (virtualAdapter is not null)
                    {
                        status.Text = "Hotspot ready - select limits, then click Start limiter";
                        status.ForeColor = Color.DarkOrange;
                    }
                    else
                    {
                        status.Text = "Waiting for Wi-Fi Hotspot...";
                        status.ForeColor = Color.DarkOrange;
                    }
                }));
            }
            catch (Exception ex)
            {
                Log.Write("Startup routine error: " + ex);
            }
        });
    }

    protected override void SetVisibleCore(bool value)
    {
        if (!IsHandleCreated && startedMinimized)
        {
            CreateHandle();
            value = false;
        }
        base.SetVisibleCore(value);
    }

    static decimal ClampDecimal(double value, decimal minimum, decimal maximum)
    {
        decimal result = (decimal)value;
        return Math.Min(maximum, Math.Max(minimum, result));
    }

    void SaveSettings()
    {
        SettingsStore.Save(new AppSettings
        {
            LimitMbps = (double)limit.Value,
            DailyLimitGb = (double)dailyLimit.Value,
            AutoStartLimiter = autoStartLimiter.Checked
        });
    }

    static long ToDailyLimitBytes(decimal gigabytes)
    {
        if (gigabytes <= 0) return 0;
        return checked((long)(gigabytes * 1_000_000_000m));
    }

    void InitDeviceList()
    {
        deviceList.Columns.Add("Device IP", 120);
        deviceList.Columns.Add("Device Name / Host", 150);
        deviceList.Columns.Add("MAC Address", 130);
        deviceList.Columns.Add("Download", 85);
        deviceList.Columns.Add("Upload", 85);
        deviceList.Columns.Add("Total Usage", 90);
        deviceList.Columns.Add("Today", 85);
        deviceList.Columns.Add("Quota", 95);
        deviceList.Columns.Add("Current Speed", 95);
        deviceList.Columns.Add("Last Active", 80);

        typeof(Control).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.SetValue(deviceList, true, null);
    }

    void InitTrayIcon()
    {
        try
        {
            trayIcon.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch
        {
            trayIcon.Icon = SystemIcons.Application;
        }

        trayIcon.Text = "Hotspot Limiter - 4.0 Mbps Cap";
        trayIcon.Visible = true;
        trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var openItem = new ToolStripMenuItem("Open Hotspot Limiter", null, (_, _) => RestoreFromTray()) { Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        var startItem = new ToolStripMenuItem("Start Limiter", null, (_, _) => StartLimiter());
        var stopItem = new ToolStripMenuItem("Stop Limiter", null, (_, _) => StopLimiter());
        var toggleHotspotItem = new ToolStripMenuItem("Toggle Mobile Hotspot", null, async (_, _) => await ToggleHotspotAsync());
        var exitItem = new ToolStripMenuItem("Exit", null, (_, _) =>
        {
            isExiting = true;
            Close();
        });

        trayMenu.Items.Add(openItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(startItem);
        trayMenu.Items.Add(stopItem);
        trayMenu.Items.Add(toggleHotspotItem);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(exitItem);

        trayIcon.ContextMenuStrip = trayMenu;
    }

    void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    async Task ToggleHotspotAsync()
    {
        toggleHotspot.Enabled = false;
        hotspotStatusLabel.Text = "Hotspot: Changing...";
        bool isOn = await HotspotController.IsHotspotOnAsync();
        if (isOn)
        {
            await HotspotController.StopHotspotAsync();
            hotspotWasOn = false;
            limiterManuallyStopped = false;
            StopLimiter(false);
            hotspotStatusLabel.Text = "Hotspot: Off";
            hotspotStatusLabel.ForeColor = Color.DarkOrange;
        }
        else
        {
            await HotspotController.StartHotspotAsync();
            hotspotStatusLabel.Text = "Hotspot: On (Wi-Fi)";
            hotspotStatusLabel.ForeColor = Color.DarkGreen;
            hotspotWasOn = true;
            limiterManuallyStopped = false;
        }
        LoadAdapters();
        if (!isOn && autoStartLimiter.Checked)
        {
            StartLimiter();
        }
        toggleHotspot.Enabled = true;
    }

    static NetworkInterface? FindHotspotAdapter()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();

        var hotspotByIp = all.FirstOrDefault(n => n.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString().StartsWith("192.168.137.")));
        if (hotspotByIp is not null) return hotspotByIp;

        var wifiDirect = all.FirstOrDefault(n => n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                                                 n.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase));
        if (wifiDirect is not null) return wifiDirect;

        return null;
    }

    static NetworkInterface? FindUplinkAdapter(NetworkInterface? hotspot)
    {
        string? hotspotId = hotspot?.Id;
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.Id != hotspotId)
            .ToList();

        var withGateway = candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0);
        if (withGateway is not null) return withGateway;

        return candidates.FirstOrDefault();
    }

    void LoadAdapters()
    {
        var currentSelected = (adapter.SelectedItem as AdapterItem)?.Interface.Id;
        adapter.Items.Clear();

        var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();

        var hotspotAdapters = new List<NetworkInterface>();
        var wifiAdapters = new List<NetworkInterface>();
        var otherAdapters = new List<NetworkInterface>();

        foreach (var n in all)
        {
            var props = n.GetIPProperties();
            var ips = props.UnicastAddresses.Select(x => x.Address.ToString()).ToList();

            bool isHotspot = ips.Any(x => x.StartsWith("192.168.137.")) ||
                             n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) ||
                             n.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase);

            bool isWifi = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                          n.Description.Contains("Wireless", StringComparison.OrdinalIgnoreCase) ||
                          n.Description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase);

            if (isHotspot)
            {
                hotspotAdapters.Add(n);
            }
            else if (isWifi)
            {
                wifiAdapters.Add(n);
            }
            else
            {
                otherAdapters.Add(n);
            }
        }

        foreach (var h in hotspotAdapters) adapter.Items.Add(new AdapterItem(h));
        foreach (var w in wifiAdapters) adapter.Items.Add(new AdapterItem(w));
        foreach (var o in otherAdapters) adapter.Items.Add(new AdapterItem(o));

        if (adapter.Items.Count > 0)
        {
            int index = 0;
            if (!string.IsNullOrEmpty(currentSelected))
            {
                for (int i = 0; i < adapter.Items.Count; i++)
                {
                    if ((adapter.Items[i] as AdapterItem)?.Interface.Id == currentSelected)
                    {
                        index = i;
                        break;
                    }
                }
            }
            adapter.SelectedIndex = index;
        }
    }

    void StartLimiter()
    {
        limiterManuallyStopped = false;
        var virtualAdapter = FindHotspotAdapter();
        if (virtualAdapter is null)
        {
            status.Text = "Waiting for Wi-Fi Hotspot...";
            status.ForeColor = Color.DarkOrange;
            Log.Write("StartLimiter: no hotspot virtual adapter found.");
            return;
        }
        StartLimiter(virtualAdapter, FindUplinkAdapter(virtualAdapter));
    }

    void StartLimiter(NetworkInterface virtualAdapter, NetworkInterface? uplink)
    {
        try
        {
            StopLimiter(false);

            uint vIdx = GetInterfaceIndex(virtualAdapter);
            uint? uIdx = uplink is not null ? GetInterfaceIndex(uplink) : null;

            string filter = (uIdx is not null && uIdx.Value != vIdx)
                ? $"ifIdx == {vIdx} || ifIdx == {uIdx.Value}"
                : $"ifIdx == {vIdx}";

            activeVirtualIdx = vIdx;
            activeUplinkIdx = uIdx;
            long dailyLimitBytes = ToDailyLimitBytes(dailyLimit.Value);
            limiter = new Limiter(filter, (double)limit.Value, dailyLimitBytes);
            limiter.Start();

            string targetName = uplink is not null ? $"{virtualAdapter.Name} + {uplink.Name}" : virtualAdapter.Name;
            string quotaText = dailyLimitBytes > 0 ? $"{FormatBytes(dailyLimitBytes)} / device / day" : "daily quota off";
            status.Text = $"Active on {targetName} ({limit.Value:0.0} Mbps cap, {quotaText})";
            status.ForeColor = Color.DarkGreen;
            trayIcon.Text = $"Hotspot Limiter ({limit.Value:0.0} Mbps)";
            Log.Write($"Limiter started. Filter: {filter}");

            start.Enabled = false;
            stop.Enabled = true;
            adapter.Enabled = false;
            limit.Enabled = false;
            dailyLimit.Enabled = false;
        }
        catch (Exception ex)
        {
            status.Text = "Error: " + ex.Message;
            status.ForeColor = Color.Red;
            Log.Write("StartLimiter error: " + ex);
            if (!startedMinimized)
            {
                MessageBox.Show(this, ex.Message + "\n\nPlease ensure you run HotspotLimiter as Administrator.", "Hotspot Limiter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    void StopLimiter(bool userInitiated = true)
    {
        if (userInitiated)
        {
            limiterManuallyStopped = true;
        }
        limiter?.Dispose();
        limiter = null;
        activeVirtualIdx = 0;
        activeUplinkIdx = null;
        status.Text = "Stopped";
        status.ForeColor = Color.Black;
        trayIcon.Text = "Hotspot Limiter - Stopped";
        start.Enabled = true;
        stop.Enabled = false;
        adapter.Enabled = true;
        limit.Enabled = true;
        dailyLimit.Enabled = true;
    }

    void ResetAllStats()
    {
        limiter?.ResetStats();
        deviceList.Items.Clear();
        usage.Text = "Total Forwarded: 0.0 MB | Devices: 0";
    }

    void UpdateStats()
    {
        if (limiter is null) return;

        var devices = limiter.GetDevicesSnapshot();
        long totalBytes = limiter.TotalBytes;
        string quotaText = limiter.DailyLimitBytes > 0 ? $" | Daily/device: {FormatBytes(limiter.DailyLimitBytes)}" : "";
        usage.Text = $"Total Forwarded: {FormatBytes(totalBytes)} | Active Devices: {devices.Count}{quotaText}";

        deviceList.BeginUpdate();
        try
        {
            var existingItems = new Dictionary<string, ListViewItem>();
            foreach (ListViewItem item in deviceList.Items)
            {
                existingItems[item.Text] = item;
            }

            var now = DateTime.UtcNow;
            foreach (var d in devices.Values.OrderByDescending(x => x.TotalBytes))
            {
                var secondsAgo = (int)(now - d.LastSeen).TotalSeconds;
                string activeText = secondsAgo < 5 ? "Active now" : $"{secondsAgo}s ago";
                string speedText = FormatSpeed(d.CurrentSpeedBps);

                if (existingItems.TryGetValue(d.IpAddress, out var item))
                {
                    item.SubItems[1].Text = d.HostName;
                    item.SubItems[2].Text = d.MacAddress;
                    item.SubItems[3].Text = FormatBytes(d.DownloadBytes);
                    item.SubItems[4].Text = FormatBytes(d.UploadBytes);
                    item.SubItems[5].Text = FormatBytes(d.TotalBytes);
                    item.SubItems[6].Text = FormatBytes(d.TodayBytes);
                    item.SubItems[7].Text = d.QuotaReached ? "LIMIT REACHED" :
                        (limiter.DailyLimitBytes > 0 ? FormatBytes(limiter.DailyLimitBytes - Math.Min(d.TodayBytes, limiter.DailyLimitBytes)) + " left" : "Off");
                    item.SubItems[8].Text = speedText;
                    item.SubItems[9].Text = activeText;
                }
                else
                {
                    var newItem = new ListViewItem(d.IpAddress);
                    newItem.SubItems.Add(d.HostName);
                    newItem.SubItems.Add(d.MacAddress);
                    newItem.SubItems.Add(FormatBytes(d.DownloadBytes));
                    newItem.SubItems.Add(FormatBytes(d.UploadBytes));
                    newItem.SubItems.Add(FormatBytes(d.TotalBytes));
                    newItem.SubItems.Add(FormatBytes(d.TodayBytes));
                    newItem.SubItems.Add(d.QuotaReached ? "LIMIT REACHED" :
                        (limiter.DailyLimitBytes > 0 ? FormatBytes(limiter.DailyLimitBytes - Math.Min(d.TodayBytes, limiter.DailyLimitBytes)) + " left" : "Off"));
                    newItem.SubItems.Add(speedText);
                    newItem.SubItems.Add(activeText);
                    deviceList.Items.Add(newItem);
                }
            }
        }
        finally
        {
            deviceList.EndUpdate();
        }
    }

    static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024.0):0.0} MB";
        return $"{bytes / (1024.0 * 1024.0 * 1024.0):0.00} GB";
    }

    static string FormatSpeed(double bytesPerSec)
    {
        double bitsPerSec = bytesPerSec * 8.0;
        if (bitsPerSec < 1000) return $"{bitsPerSec:0} bps";
        if (bitsPerSec < 1_000_000) return $"{bitsPerSec / 1000.0:0.0} Kbps";
        return $"{bitsPerSec / 1_000_000.0:0.00} Mbps";
    }

    static uint GetInterfaceIndex(NetworkInterface ni)
    {
        var props = ni.GetIPProperties();
        var v4 = props.GetIPv4Properties();
        if (v4 is null) throw new InvalidOperationException("The selected adapter has no IPv4 interface index.");
        return (uint)v4.Index;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && minimizeToTray.Checked)
        {
            HideToTray();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!isExiting && minimizeToTray.Checked && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        limiter?.Dispose();
        DailyUsageStore.Flush();
        trayIcon.Visible = false;
        trayIcon.Dispose();
        Log.Write("HotspotLimiter exiting.");
        base.OnFormClosing(e);
    }

    sealed class AdapterItem
    {
        public NetworkInterface Interface { get; }
        public AdapterItem(NetworkInterface i) => Interface = i;
        public override string ToString() => $"{Interface.Name} — {Interface.Description}";
    }
}

public sealed class DeviceStats
{
    public string IpAddress { get; set; } = "";
    // Windows Mobile Hotspot assigns addresses from this subnet. The IP is used
    // as the quota key because it is available before ARP/DNS resolution finishes.
    public string QuotaKey { get; set; } = "";
    public string MacAddress { get; set; } = "Resolving...";
    public string HostName { get; set; } = "Device";
    public long DownloadBytes;
    public long UploadBytes;
    public long TotalBytes => Interlocked.Read(ref DownloadBytes) + Interlocked.Read(ref UploadBytes);

    public long PeriodBytes;
    public double CurrentSpeedBps;
    public DateTime LastSeen = DateTime.UtcNow;
    public DateTime FirstSeen = DateTime.UtcNow;
    public bool QuotaReached;
    public long TodayBytes => DailyUsageStore.Get(QuotaKey.Length > 0 ? QuotaKey : IpAddress);
}

public sealed class Limiter : IDisposable
{
    const int MaxPacket = 65535;
    const double BurstSeconds = 0.25;
    readonly string filter;
    readonly double bytesPerSecond;
    readonly double maxTokens;
    readonly long dailyLimitBytes;
    IntPtr handle = IntPtr.Zero;
    CancellationTokenSource cts = new();
    long totalBytes;
    double tokens;
    long lastTicks;
    readonly object gate = new();

    readonly ConcurrentDictionary<string, DeviceStats> devices = new(StringComparer.OrdinalIgnoreCase);
    System.Threading.Timer? speedCalcTimer;

    public long TotalBytes => Interlocked.Read(ref totalBytes);
    public long DailyLimitBytes => dailyLimitBytes;

    public Limiter(string filter, double mbps, long dailyLimitBytes)
    {
        this.filter = filter;
        bytesPerSecond = mbps * 1_000_000.0 / 8.0;
        maxTokens = Math.Max(bytesPerSecond * BurstSeconds, 8192);
        this.dailyLimitBytes = dailyLimitBytes;
        tokens = maxTokens;
        lastTicks = Stopwatch.GetTimestamp();

        speedCalcTimer = new System.Threading.Timer(_ => CalculateLiveSpeeds(), null, 1000, 1000);
    }

    public ConcurrentDictionary<string, DeviceStats> GetDevicesSnapshot() => devices;

    public void ResetStats()
    {
        Interlocked.Exchange(ref totalBytes, 0);
        devices.Clear();
    }

    void CalculateLiveSpeeds()
    {
        foreach (var d in devices.Values)
        {
            long currentPeriod = Interlocked.Exchange(ref d.PeriodBytes, 0);
            d.CurrentSpeedBps = currentPeriod;
        }
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows only.");
        handle = WinDivert.Open(filter, WinDivertLayer.NetworkForward, 0, 0);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            int err = Marshal.GetLastWin32Error();
            Log.Write($"WinDivertOpen failed. Filter: {filter}, Win32 error: {err}");
            throw new InvalidOperationException($"WinDivertOpen failed. Win32 error: {err}");
        }
        WinDivert.SetParam(handle, WinDivertParam.QueueLength, 16384);
        WinDivert.SetParam(handle, WinDivertParam.QueueSize, 32 * 1024 * 1024);
        Task.Run(Run, cts.Token);
    }

    async Task Run()
    {
        var packet = new byte[MaxPacket];
        var addr = new WinDivertAddress();
        while (!cts.IsCancellationRequested)
        {
            uint len = 0;
            if (!WinDivert.Recv(handle, packet, (uint)packet.Length, ref len, ref addr))
            {
                if (cts.IsCancellationRequested) break;
                continue;
            }
            if (len == 0) continue;

            if (!TryReservePacket(packet, (int)len, out var device, out bool isDownload))
            {
                // Dropping the packet makes the client stop receiving data once
                // its daily quota is reached. TCP/QUIC will retry, but retries
                // are not counted as additional usage.
                continue;
            }

            await Consume((int)len).ConfigureAwait(false);
            uint sent = 0;
            if (!WinDivert.Send(handle, packet, len, ref sent, ref addr))
            {
                continue;
            }
            Interlocked.Add(ref totalBytes, len);
            if (device is not null)
            {
                device.LastSeen = DateTime.UtcNow;
                Interlocked.Add(ref device.PeriodBytes, (long)len);
                if (isDownload)
                {
                    Interlocked.Add(ref device.DownloadBytes, (long)len);
                }
                else
                {
                    Interlocked.Add(ref device.UploadBytes, (long)len);
                }
            }
        }
    }

    bool TryReservePacket(byte[] packet, int len, out DeviceStats? device, out bool isDownload)
    {
        device = null;
        isDownload = false;
        if (len < 20) return true;
        int version = packet[0] >> 4;

        string? clientIp = null;

        if (version == 4)
        {
            string srcIp = $"{packet[12]}.{packet[13]}.{packet[14]}.{packet[15]}";
            string dstIp = $"{packet[16]}.{packet[17]}.{packet[18]}.{packet[19]}";

            if (IsClientIp(srcIp))
            {
                clientIp = srcIp;
                isDownload = false;
            }
            else if (IsClientIp(dstIp))
            {
                clientIp = dstIp;
                isDownload = true;
            }
        }

        if (clientIp is null) return true;

        device = devices.GetOrAdd(clientIp, ip =>
        {
            var newDevice = new DeviceStats { IpAddress = ip, QuotaKey = ip };
            Task.Run(() => ResolveDeviceInfo(newDevice));
            return newDevice;
        });

        if (dailyLimitBytes > 0 && !DailyUsageStore.TryReserve(device.QuotaKey, len, dailyLimitBytes))
        {
            device.QuotaReached = true;
            device.LastSeen = DateTime.UtcNow;
            return false;
        }

        if (dailyLimitBytes <= 0)
        {
            DailyUsageStore.Add(device.QuotaKey, len);
        }

        return true;
    }

    bool IsClientIp(string ip) => ip.StartsWith("192.168.137.") && !ip.EndsWith(".1") && !ip.EndsWith(".255");

    static void ResolveDeviceInfo(DeviceStats device)
    {
        try
        {
            if (IPAddress.TryParse(device.IpAddress, out var ipAddr))
            {
                byte[] mac = new byte[6];
                uint macLen = (uint)mac.Length;
                uint destIp = BitConverter.ToUInt32(ipAddr.GetAddressBytes(), 0);
                if (SendARP(destIp, 0, mac, ref macLen) == 0 && macLen == 6)
                {
                    device.MacAddress = string.Format("{0:X2}:{1:X2}:{2:X2}:{3:X2}:{4:X2}:{5:X2}",
                        mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
                }
                else
                {
                    device.MacAddress = "—";
                }
            }
        }
        catch
        {
            device.MacAddress = "—";
        }

        try
        {
            var hostEntry = Dns.GetHostEntry(device.IpAddress);
            if (!string.IsNullOrWhiteSpace(hostEntry.HostName))
            {
                string name = hostEntry.HostName;
                if (name.Contains('.')) name = name.Split('.')[0];
                device.HostName = name;
            }
        }
        catch
        {
            if (device.HostName == "Device")
            {
                device.HostName = $"Client ({device.IpAddress.Split('.').Last()})";
            }
        }
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint DestIP, uint SrcIP, [Out] byte[] pMacAddr, ref uint PhysAddrLen);

    async Task Consume(int bytes)
    {
        while (!cts.IsCancellationRequested)
        {
            double waitSeconds;
            lock (gate)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsed = (now - lastTicks) / (double)Stopwatch.Frequency;
                if (elapsed > 0)
                {
                    tokens = Math.Min(maxTokens, tokens + elapsed * bytesPerSecond);
                    lastTicks = now;
                }
                if (tokens >= bytes)
                {
                    tokens -= bytes;
                    return;
                }
                waitSeconds = (bytes - tokens) / bytesPerSecond;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Max(waitSeconds, 0.001), 0.25)), cts.Token).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        cts.Cancel();
        speedCalcTimer?.Dispose();
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
        {
            WinDivert.Shutdown(handle, 3);
            WinDivert.Close(handle);
            handle = IntPtr.Zero;
        }
        cts.Dispose();
        DailyUsageStore.Flush();
    }
}

public static class HotspotController
{
    public static async Task<bool> IsHotspotOnAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (n.OperationalStatus != OperationalStatus.Up) continue;
                    var props = n.GetIPProperties();
                    if (props.UnicastAddresses.Any(x => x.Address.ToString().StartsWith("192.168.137.")))
                        return true;
                }
            }
            catch { }
            return false;
        });
    }

    public static async Task<bool> StartHotspotAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string script = @"
                    $ErrorActionPreference = 'Continue'
                    Add-Type -AssemblyName System.Runtime.WindowsRuntime
                    $profile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]::GetInternetConnectionProfile()
                    if ($null -eq $profile) {
                        Write-Output 'RESULT: NO_INTERNET_PROFILE'
                        exit 1
                    }
                    $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]::CreateFromConnectionProfile($profile)
                    if ($mgr.TetheringOperationalState -eq [Windows.Networking.NetworkOperators.TetheringOperationalState]::On) {
                        Write-Output 'RESULT: ALREADY_ON'
                        exit 0
                    }
                    $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
                    })[0]
                    $op = $mgr.StartTetheringAsync()
                    $t = $asTask.MakeGenericMethod([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]).Invoke($null, @($op))
                    if ($t.Wait(20000)) {
                        Write-Output ('RESULT: ' + $t.Result.Status)
                    } else {
                        Write-Output 'RESULT: TIMEOUT'
                        exit 1
                    }
                ";

                byte[] bytes = Encoding.Unicode.GetBytes(script);
                string encoded = Convert.ToBase64String(bytes);

                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Log.Write("Hotspot start: failed to launch PowerShell.");
                    return false;
                }
                string output = p.StandardOutput.ReadToEnd();
                string err = p.StandardError.ReadToEnd();
                p.WaitForExit(25000);
                Log.Write($"Hotspot start output: {output.Trim()}{(string.IsNullOrWhiteSpace(err) ? "" : " | stderr: " + err.Trim())}");
                return output.Contains("ALREADY_ON") || output.Contains("Success") || output.Contains("RESULT: Success");
            }
            catch (Exception ex)
            {
                Log.Write("Hotspot start exception: " + ex.Message);
                return false;
            }
        });
    }

    public static async Task<bool> StopHotspotAsync()
    {
        return await Task.Run(() =>
        {
            try
            {
                string script = @"
                    Add-Type -AssemblyName System.Runtime.WindowsRuntime
                    $profile = [Windows.Networking.Connectivity.NetworkInformation, Windows.Networking.Connectivity, ContentType = WindowsRuntime]::GetInternetConnectionProfile()
                    if ($null -ne $profile) {
                        $mgr = [Windows.Networking.NetworkOperators.NetworkOperatorTetheringManager, Windows.Networking.NetworkOperators, ContentType = WindowsRuntime]::CreateFromConnectionProfile($profile)
                        if ($mgr.TetheringOperationalState -eq [Windows.Networking.NetworkOperators.TetheringOperationalState]::On) {
                            $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
                                $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
                            })[0]
                            $op = $mgr.StopTetheringAsync()
                            $t = $asTask.MakeGenericMethod([Windows.Networking.NetworkOperators.NetworkOperatorTetheringOperationResult]).Invoke($null, @($op))
                            $t.Wait(10000)
                        }
                    }
                ";

                byte[] bytes = Encoding.Unicode.GetBytes(script);
                string encoded = Convert.ToBase64String(bytes);

                var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(12000);
                Log.Write("Hotspot stop requested.");
                return true;
            }
            catch
            {
                return false;
            }
        });
    }
}

public static class StartupManager
{
    const string TaskName = "HotspotLimiterAutoStart";

    public static bool IsEnabled()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", $"/query /tn \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
            if (p?.ExitCode == 0) return true;

            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (k?.GetValue("HotspotLimiter") != null) return true;
        }
        catch { }

        return false;
    }

    public static void SetEnabled(bool enabled)
    {
        string exePath = Application.ExecutablePath;
        string cmd = $"\"{exePath}\" --tray";

        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enabled)
            {
                k.SetValue("HotspotLimiter", cmd);
            }
            else
            {
                k.DeleteValue("HotspotLimiter", false);
            }
        }
        catch { }

        try
        {
            if (enabled)
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/create /tn \"{TaskName}\" /tr \"{cmd}\" /sc onlogon /delay 0000:10 /rl highest /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(3000);
            }
            else
            {
                var psi = new ProcessStartInfo("schtasks.exe", $"/delete /tn \"{TaskName}\" /f")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(3000);
            }
        }
        catch { }
    }
}

public sealed class AppSettings
{
    public double LimitMbps { get; set; } = 4.0;
    public double DailyLimitGb { get; set; } = 1.0;
    public bool AutoStartLimiter { get; set; } = false;
}

static class SettingsStore
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotspotLimiter", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
            }
        }
        catch
        {
            // Fall back to safe defaults if the settings file is incomplete/corrupt.
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Settings are optional; limiter operation must continue if saving fails.
        }
    }
}

static class DailyUsageStore
{
    sealed class UsageFile
    {
        public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");
        public Dictionary<string, long> DeviceBytes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly object Gate = new();
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HotspotLimiter", "daily-usage.json");
    static string date = DateTime.Now.ToString("yyyy-MM-dd");
    static Dictionary<string, long> deviceBytes = new(StringComparer.OrdinalIgnoreCase);
    static DateTime lastSaveUtc = DateTime.MinValue;
    static long unsavedBytes;

    static DailyUsageStore()
    {
        Load();
    }

    public static long Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return 0;
        lock (Gate)
        {
            EnsureCurrentDayLocked();
            return deviceBytes.TryGetValue(key, out long value) ? value : 0;
        }
    }

    public static bool TryReserve(string key, long bytes, long limitBytes)
    {
        if (string.IsNullOrWhiteSpace(key) || bytes <= 0) return true;
        lock (Gate)
        {
            EnsureCurrentDayLocked();
            long current = deviceBytes.TryGetValue(key, out long value) ? value : 0;
            if (limitBytes > 0 && (current > limitBytes || bytes > limitBytes - current))
            {
                return false;
            }

            deviceBytes[key] = checked(current + bytes);
            unsavedBytes = checked(unsavedBytes + bytes);
            MaybeSaveLocked();
            return true;
        }
    }

    public static void Add(string key, long bytes)
    {
        if (string.IsNullOrWhiteSpace(key) || bytes <= 0) return;
        lock (Gate)
        {
            EnsureCurrentDayLocked();
            long current = deviceBytes.TryGetValue(key, out long value) ? value : 0;
            deviceBytes[key] = checked(current + bytes);
            unsavedBytes = checked(unsavedBytes + bytes);
            MaybeSaveLocked();
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            EnsureCurrentDayLocked();
            SaveLocked();
        }
    }

    static void Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                var stored = JsonSerializer.Deserialize<UsageFile>(File.ReadAllText(FilePath));
                if (stored is not null && stored.Date == date && stored.DeviceBytes is not null)
                {
                    deviceBytes = new Dictionary<string, long>(stored.DeviceBytes, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch
            {
                deviceBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    static void EnsureCurrentDayLocked()
    {
        string today = DateTime.Now.ToString("yyyy-MM-dd");
        if (today == date) return;
        date = today;
        deviceBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        unsavedBytes = 0;
        SaveLocked();
    }

    static void MaybeSaveLocked()
    {
        if (unsavedBytes >= 1_000_000 || DateTime.UtcNow - lastSaveUtc >= TimeSpan.FromSeconds(15))
        {
            SaveLocked();
        }
    }

    static void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tempPath = FilePath + ".tmp";
            var stored = new UsageFile { Date = date, DeviceBytes = new Dictionary<string, long>(deviceBytes) };
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, FilePath, true);
            lastSaveUtc = DateTime.UtcNow;
            unsavedBytes = 0;
        }
        catch
        {
            // The in-memory quota remains active even if persistence is unavailable.
        }
    }
}

static class Log
{
    static readonly object Lock = new();
    static string filePath = "";

    public static void Init()
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotspotLimiter");
            Directory.CreateDirectory(dir);
            filePath = Path.Combine(dir, "HotspotLimiter.log");
            Write("========== Log session start ==========");
        }
        catch { }
    }

    public static void Write(string message)
    {
        if (string.IsNullOrEmpty(filePath)) return;
        try
        {
            lock (Lock)
            {
                File.AppendAllText(filePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch { }
    }
}

static class WinDivert
{
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertOpen", CharSet = CharSet.Ansi)]
    public static extern IntPtr Open(string filter, WinDivertLayer layer, short priority, ulong flags);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertRecv")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Recv(IntPtr handle, [Out] byte[] packet, uint packetLen, ref uint recvLen, ref WinDivertAddress addr);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertSend")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Send(IntPtr handle, byte[] packet, uint packetLen, ref uint sendLen, ref WinDivertAddress addr);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertClose")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Close(IntPtr handle);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertShutdown")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Shutdown(IntPtr handle, uint how);
    [DllImport("WinDivert.dll", CallingConvention = CallingConvention.Winapi, SetLastError = true, EntryPoint = "WinDivertSetParam")]
    [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetParam(IntPtr handle, WinDivertParam param, ulong value);
}

enum WinDivertLayer : int { Network = 0, NetworkForward = 1, Flow = 2, Socket = 3, Reflect = 4 }
enum WinDivertParam : int { QueueLength = 0, QueueTime = 1, QueueSize = 2 }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct WinDivertAddress
{
    public long Timestamp;
    public uint Flags;
    public uint Reserved2;
    public ulong Reserved3_0;
    public ulong Reserved3_1;
    public ulong Reserved3_2;
    public ulong Reserved3_3;
    public ulong Reserved3_4;
    public ulong Reserved3_5;
    public ulong Reserved3_6;
    public ulong Reserved3_7;
}
