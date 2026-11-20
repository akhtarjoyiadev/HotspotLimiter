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
        if (args.Contains("--self-test")) { Environment.ExitCode = SelfTests.Run(); return; }
        Log.Init();
        if (args.Contains("--verify-driver"))
        {
            ExtractResource("HotspotLimiter.WinDivert.dll", "WinDivert.dll");
            ExtractResource("HotspotLimiter.WinDivert64.sys", "WinDivert64.sys");
            Environment.ExitCode = SelfTests.VerifyDriver(); return;
        }
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
    readonly NumericUpDown dailyLimit = new() { Minimum = 0.0m, Maximum = 1000.0m, Value = 0.0m, DecimalPlaces = 2, Increment = 0.25m, Width = 80 };
    readonly Button start = new() { Text = "▶ Start limiter", Width = 110, Height = 28 };
    readonly Button stop = new() { Text = "⏹ Stop", Width = 80, Height = 28, Enabled = false };
    readonly Button toggleHotspot = new() { Text = "📶 Toggle Hotspot", Width = 130, Height = 28 };
    readonly Button repairHotspot = new() { Text = "Repair hotspot", AutoSize = true };
    readonly Button diagnostics = new() { Text = "Diagnostics", AutoSize = true };
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
    bool autoDetectRunning = false;
    bool hotspotOperationRunning;
    bool suppressAutoStart;
    DateTime nextStartAttemptUtc;
    int startFailures;
    readonly bool startedMinimized;

    public MainForm(bool startMinimized)
    {

        this.startedMinimized = startMinimized;
        var savedSettings = SettingsStore.Load();
        limit.Value = ClampDecimal(savedSettings.LimitMbps, limit.Minimum, limit.Maximum);
        dailyLimit.Value = ClampDecimal(savedSettings.DailyLimitGb, dailyLimit.Minimum, dailyLimit.Maximum);
        autoStartLimiter.Checked = savedSettings.AutoStartLimiter;
        autoStartHotspot.Checked = savedSettings.AutoStartHotspot;
        minimizeToTray.Checked = savedSettings.MinimizeToTray;
        Text = $"Hotspot Limiter & Wi-Fi Tracker ({limit.Value:0.0} Mbps)";
        Width = 1100; Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1000, 500);

        InitTrayIcon();
        InitDeviceList();

        // Layout
        var mainContainer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
        mainContainer.RowStyles.Add(new RowStyle(SizeType.Absolute, 205));
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

        var buttonFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Fill };
        buttonFlow.Controls.Add(start);
        buttonFlow.Controls.Add(stop);
        buttonFlow.Controls.Add(toggleHotspot);
        buttonFlow.Controls.Add(resetStats);
        buttonFlow.Controls.Add(repairHotspot);
        buttonFlow.Controls.Add(diagnostics);
        topPanel.SetColumnSpan(buttonFlow, 2);
        topPanel.Controls.Add(buttonFlow, 1, 1);

        var checkFlow = new FlowLayoutPanel { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight };
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
        repairHotspot.Click += async (_, _) => await RepairHotspotAsync();
        diagnostics.Click += async (_, _) =>
        {
            diagnostics.Enabled = false;
            try { var path = await Diagnostics.WriteReportAsync(); Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Diagnostics"); }
            finally { diagnostics.Enabled = true; }
        };
        startup.Checked = StartupManager.IsEnabled();
        startup.CheckedChanged += (_, _) => StartupManager.SetEnabled(startup.Checked);
        limit.ValueChanged += (_, _) =>
        {
            SaveSettings();
            UpdateLimitCaption();
        };
        dailyLimit.ValueChanged += (_, _) => SaveSettings();
        autoStartLimiter.CheckedChanged += (_, _) => SaveSettings();
        autoStartHotspot.CheckedChanged += (_, _) => { suppressAutoStart = false; nextStartAttemptUtc = DateTime.MinValue; SaveSettings(); };
        minimizeToTray.CheckedChanged += (_, _) => SaveSettings();

        statsTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        statsTimer.Tick += (_, _) => UpdateStats();
        statsTimer.Start();

        autoDetectTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        autoDetectTimer.Tick += async (_, _) => await MonitorHotspotAsync();
        autoDetectTimer.Start(); // Also works when launched hidden in the system tray.
        Shown += async (_, _) => await MonitorHotspotAsync();
    }

    async Task MonitorHotspotAsync()
    {
        if (autoDetectRunning || hotspotOperationRunning || isExiting) return;
        autoDetectRunning = true;
        try
        {
            bool on = await HotspotController.IsHotspotOnAsync();
            if (isExiting || IsDisposed) return;
            var state = HotspotController.LastState;
            // Unknown is NOT Off: don't continually restart a working AP when querying Windows fails.
            if (state.Success && state.State == "Off" && autoStartHotspot.Checked && !suppressAutoStart && DateTime.UtcNow >= nextStartAttemptUtc)
            {
                on = await HotspotController.StartHotspotAsync();
                startFailures = on ? 0 : Math.Min(startFailures + 1, 6);
                nextStartAttemptUtc = DateTime.UtcNow.AddSeconds(on ? 15 : Math.Min(300, 15 * Math.Pow(2, startFailures)));
                state = HotspotController.LastState;
            }
            ShowHotspotState();
            if (on)
            {
                var nic = FindHotspotAdapter();
                if (nic != null)
                {
                    uint index = GetInterfaceIndex(nic);
                    if (!limiterManuallyStopped && ((limiter == null && autoStartLimiter.Checked) || (limiter != null && activeVirtualIdx != index)))
                    {
                        LoadAdapters(); StartLimiter(nic, FindUplinkAdapter(nic));
                    }
                    else if (limiter == null) status.Text = "Hotspot ready — limiter off (internet unrestricted)";
                }
            }
            else if (state.Success && state.State == "Off")
            {
                if (limiter != null) StopLimiter(false);
            }
            if (limiter?.Failure != null)
            {
                string failure = limiter.Failure;
                StopLimiter();
                status.Text = "Limiter stopped safely: " + failure;
                status.ForeColor = Color.Red;
            }
        }
        catch (Exception ex) { Log.Write("Hotspot monitor: " + ex); }
        finally { autoDetectRunning = false; }
    }

    void ShowHotspotState()
    {
        var state = HotspotController.LastState;
        hotspotStatusLabel.Text = state.IsOn ? $"Hotspot: On | Clients {state.ClientCount}/{state.MaxClientCount}" : $"Hotspot: {state.State}";
        hotspotStatusLabel.ForeColor = state.IsOn ? Color.DarkGreen : Color.DarkOrange;
        if (!state.Success) { status.Text = state.Error; status.ForeColor = Color.Red; }
    }

    async Task RepairHotspotAsync()
    {
        if (hotspotOperationRunning || autoDetectRunning) return;
        if (MessageBox.Show(this, "Reconnect the hotspot in 2.4 GHz compatibility mode? Connected devices will briefly disconnect. Your Wi-Fi name and password will be kept. The limiter will stay off so you can test connectivity first.", "Repair hotspot", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        hotspotOperationRunning = true;
        repairHotspot.Enabled = toggleHotspot.Enabled = false;
        try
        {
            StopLimiter();
            status.Text = "Repairing hotspot...";
            bool ok = await HotspotController.RepairAsync();
            suppressAutoStart = !ok;
            ShowHotspotState(); LoadAdapters();
            if (ok) status.Text = "Hotspot repaired — reconnect your device; limiter is off for testing.";
        }
        finally { hotspotOperationRunning = false; repairHotspot.Enabled = toggleHotspot.Enabled = true; }
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
        if (!double.IsFinite(value)) return minimum;
        decimal result = (decimal)Math.Clamp(value, (double)minimum, (double)maximum);
        return Math.Min(maximum, Math.Max(minimum, result));
    }

    void SaveSettings()
    {
        SettingsStore.Save(new AppSettings
        {
            LimitMbps = (double)limit.Value,
            DailyLimitGb = (double)dailyLimit.Value,
            AutoStartLimiter = autoStartLimiter.Checked,
            AutoStartHotspot = autoStartHotspot.Checked,
            MinimizeToTray = minimizeToTray.Checked
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

        trayIcon.Text = $"Hotspot Limiter - {limit.Value:0.0} Mbps Cap";
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
        if (hotspotOperationRunning || autoDetectRunning) return;
        hotspotOperationRunning = true;
        toggleHotspot.Enabled = repairHotspot.Enabled = false;
        try
        {
            bool on = await HotspotController.IsHotspotOnAsync();
            if (!HotspotController.LastState.Success) { ShowHotspotState(); return; }
            if (on)
            {
                suppressAutoStart = true; // Respect a manual Stop even when auto-start is checked.
                StopLimiter(false);
                await HotspotController.StopHotspotAsync();
            }
            else
            {
                suppressAutoStart = false;
                if (await HotspotController.StartHotspotAsync())
                {
                    limiterManuallyStopped = false;
                    if (autoStartLimiter.Checked) StartLimiter();
                }
            }
            ShowHotspotState(); LoadAdapters();
        }
        finally { hotspotOperationRunning = false; toggleHotspot.Enabled = repairHotspot.Enabled = true; }
    }

    static NetworkInterface? FindHotspotAdapter()
    {
        var all = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up).ToList();

        return all.FirstOrDefault(n =>
            (n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) || n.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase)) &&
            n.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address) && !u.Address.ToString().StartsWith("169.254.")));
    }

    static NetworkInterface? FindUplinkAdapter(NetworkInterface? hotspot)
    {
        string? hotspotId = hotspot?.Id;
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                        n.Id != hotspotId &&
                        !n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase) &&
                        !n.Name.Contains("Local Area Connection*", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 1. Prefer wired Ethernet adapter with a default gateway
        var ethernetWithGateway = candidates.FirstOrDefault(n =>
            n.NetworkInterfaceType == NetworkInterfaceType.Ethernet &&
            n.GetIPProperties().GatewayAddresses.Count > 0);
        if (ethernetWithGateway is not null) return ethernetWithGateway;

        // 2. Prefer non-Wi-Fi adapters with a gateway (prevent choosing the hotspot physical Wi-Fi adapter as uplink)
        var nonWifiWithGateway = candidates.FirstOrDefault(n =>
            n.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
            n.GetIPProperties().GatewayAddresses.Count > 0);
        if (nonWifiWithGateway is not null) return nonWifiWithGateway;

        // 3. Any candidate with a gateway
        var withGateway = candidates.FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Count > 0);
        if (withGateway is not null) return withGateway;

        // 4. Fallback to Ethernet or first candidate
        return candidates.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet) ?? candidates.FirstOrDefault();
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

            // NETWORK_FORWARD packets can arrive through a different interface
            // than the one selected by the route lookup (VPNs, Wi-Fi/Ethernet
            // changes, and ICS transitions are common examples). Capturing all
            // forwarded packets and applying the hotspot-client classification
            // in TryReservePacket prevents those packets from bypassing the cap.
            var address = virtualAdapter.GetIPProperties().UnicastAddresses.First(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
            var subnet = new HotspotSubnet(address.Address, address.IPv4Mask);
            string filter = subnet.CaptureFilter;

            activeVirtualIdx = vIdx;
            activeUplinkIdx = uIdx;
            long dailyLimitBytes = ToDailyLimitBytes(dailyLimit.Value);
            limiter = new Limiter(filter, (double)limit.Value, dailyLimitBytes, subnet);
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
            limiter?.Dispose(); limiter = null;
            limiterManuallyStopped = true;
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
        UpdateLimitCaption();
    }

    void UpdateLimitCaption()
    {
        string cap = $"{limit.Value:0.0} Mbps";
        Text = $"Hotspot Limiter & Wi-Fi Tracker ({cap})";
        if (limiter is null)
        {
            trayIcon.Text = $"Hotspot Limiter - {cap} Cap";
        }
    }

    void ResetAllStats()
    {
        limiter?.ResetStats();
        deviceList.Items.Clear();
        usage.Text = "Total Forwarded: 0.0 MB | Devices: 0";
    }

    void UpdateStats()
    {
        var connected = HotspotController.LastState.Clients;
        if (limiter is null)
        {
            usage.Text = $"Limiter off — internet unrestricted | Connected devices: {HotspotController.LastState.ClientCount}";
            deviceList.BeginUpdate();
            try
            {
                deviceList.Items.Clear();
                foreach (var client in connected)
                {
                    var row = new ListViewItem(string.IsNullOrEmpty(client.IpAddress) ? "Obtaining IP…" : client.IpAddress);
                    row.SubItems.AddRange(new[] { "Connected client", client.MacAddress, "—", "—", "—", FormatBytes(DailyUsageStore.Get(client.IpAddress)), "Not enforced", "—", "Connected" });
                    deviceList.Items.Add(row);
                }
            }
            finally { deviceList.EndUpdate(); }
            return;
        }
        limiter.RegisterConnectedClients(connected);
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

        isExiting = true;
        statsTimer?.Stop(); autoDetectTimer?.Stop();
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
    public DateTime LastIdentityAttemptUtc = DateTime.MinValue;
    public bool QuotaReached;
    public long TodayBytes => DailyUsageStore.Get(QuotaKey.Length > 0 ? QuotaKey : IpAddress);
}

public sealed class Limiter : IDisposable
{
    const int MaxPacket = 65535;
    readonly HotspotSubnet subnet;
    readonly TokenBucket bucket;
    Task? worker;
    public string? Failure { get; private set; }
    readonly string filter;

    readonly long dailyLimitBytes;
    IntPtr handle = IntPtr.Zero;
    CancellationTokenSource cts = new();
    long totalBytes;

    long lastTicks;
    readonly object gate = new();

    readonly ConcurrentDictionary<string, DeviceStats> devices = new(StringComparer.OrdinalIgnoreCase);
    System.Threading.Timer? speedCalcTimer;

    public long TotalBytes => Interlocked.Read(ref totalBytes);
    public long DailyLimitBytes => dailyLimitBytes;

    public Limiter(string filter, double mbps, long dailyLimitBytes, HotspotSubnet subnet)
    {
        this.filter = filter;
        this.subnet = subnet;
        bucket = new TokenBucket(mbps * 1_000_000.0 / 8.0);
        this.dailyLimitBytes = dailyLimitBytes;
        lastTicks = Stopwatch.GetTimestamp();

        speedCalcTimer = new System.Threading.Timer(_ => CalculateLiveSpeeds(), null, 1000, 1000);
    }

    public ConcurrentDictionary<string, DeviceStats> GetDevicesSnapshot() => devices;

    public void RegisterConnectedClients(IEnumerable<ConnectedClient> clients)
    {
        foreach (var client in clients)
        {
            if (!IPAddress.TryParse(client.IpAddress, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork) continue;
            var device = devices.GetOrAdd(client.IpAddress, address => new DeviceStats { IpAddress = address, QuotaKey = address });
            if (!string.IsNullOrWhiteSpace(client.MacAddress)) device.MacAddress = client.MacAddress;
        }
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref totalBytes, 0);
        devices.Clear();
    }

    void CalculateLiveSpeeds()
    {
        var now = DateTime.UtcNow;
        foreach (var d in devices.Values)
        {
            long currentPeriod = Interlocked.Exchange(ref d.PeriodBytes, 0);
            d.CurrentSpeedBps = currentPeriod;

            // ARP/DNS can fail briefly while a phone is joining the hotspot.
            // Retry unknown identities so a transient miss does not permanently
            // leave a client as "Client (64)" with no MAC address.
            bool identityUnknown = d.MacAddress is "Resolving..." or "—" ||
                                   d.HostName == "Device" ||
                                   d.HostName.StartsWith("Client (", StringComparison.Ordinal);
            if (identityUnknown && (now - d.LastIdentityAttemptUtc).TotalSeconds >= 10)
            {
                QueueDeviceInfoRefresh(d);
            }
        }
    }

    static void QueueDeviceInfoRefresh(DeviceStats device)
    {
        device.LastIdentityAttemptUtc = DateTime.UtcNow;
        Task.Run(() => ResolveDeviceInfo(device));
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
        WinDivert.SetParam(handle, WinDivertParam.QueueLength, 2048);
        WinDivert.SetParam(handle, WinDivertParam.QueueTime, 1000);
        WinDivert.SetParam(handle, WinDivertParam.QueueSize, 4 * 1024 * 1024);
        worker = Task.Run(Run);
    }

    async Task Run()
    {
        var packet = new byte[MaxPacket];
        var addr = new WinDivertAddress();
        try
        {
        while (!cts.IsCancellationRequested)
        {
            uint len = 0;
            if (!WinDivert.Recv(handle, packet, (uint)packet.Length, ref len, ref addr))
            {
                if (cts.IsCancellationRequested) break;
                throw new IOException($"Packet receive failed ({Marshal.GetLastWin32Error()}).");
            }
            if (len == 0) continue;

            if (!TryReservePacket(packet, (int)len, out var device, out bool isDownload))
            {
                // Dropping the packet makes the client stop receiving data once
                // its daily quota is reached. TCP/QUIC will retry, but retries
                // are not counted as additional usage.
                continue;
            }

            if (device is not null)
            {
                await Consume((int)len).ConfigureAwait(false);
            }

            uint sent = 0;
            if (!WinDivert.Send(handle, packet, len, ref sent, ref addr))
            {
                throw new IOException($"Packet reinjection failed ({Marshal.GetLastWin32Error()}).");
            }
            if (sent != len) throw new IOException("Incomplete packet reinjection.");
            if (device != null) Interlocked.Add(ref totalBytes, len);
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
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { Failure = ex.Message; Log.Write("Limiter failed open: " + ex); }
        finally
        {
            // Drain the receive queue unshaped before closing, avoiding a black-hole after worker failure.
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            {
                WinDivert.Shutdown(handle, 1);
                uint length = 0;
                while (WinDivert.Recv(handle, packet, (uint)packet.Length, ref length, ref addr))
                {
                    uint sent = 0; WinDivert.Send(handle, packet, length, ref sent, ref addr);
                }
                WinDivert.Close(handle); handle = IntPtr.Zero;
            }
        }
    }

    bool TryReservePacket(byte[] packet, int len, out DeviceStats? device, out bool isDownload)
    {
        device = null;
        isDownload = false;
        if (!subnet.TryClassify(packet, len, out string? clientIp, out isDownload) || clientIp == null) return true;

        device = devices.GetOrAdd(clientIp, ip =>
        {
            var newDevice = new DeviceStats { IpAddress = ip, QuotaKey = ip };
            QueueDeviceInfoRefresh(newDevice);
            return newDevice;
        });

        device.QuotaReached = false;
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
                lastTicks = now;
                waitSeconds = bucket.Reserve(bytes, elapsed);
                if (waitSeconds == 0) return;
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Max(waitSeconds, 0.001), 0.25)), cts.Token).ConfigureAwait(false);
        }
    }

    int disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        cts.Cancel();
        speedCalcTimer?.Dispose();
        if (handle != IntPtr.Zero && handle != new IntPtr(-1)) WinDivert.Shutdown(handle, 1);
        // Worker owns the native handle; never close/reuse it while Recv/Send is in flight.
        if (worker != null) worker.GetAwaiter().GetResult();
        else if (handle != IntPtr.Zero && handle != new IntPtr(-1)) { WinDivert.Close(handle); handle = IntPtr.Zero; }
        cts.Dispose();
        DailyUsageStore.Flush();
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
        string? xmlPath = null;
        try
        {
            var info = new ProcessStartInfo("schtasks.exe") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            if (enabled)
            {
                string exe = System.Security.SecurityElement.Escape(Application.ExecutablePath)!;
                string user = System.Security.SecurityElement.Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name)!;
                string xml = $"""
                    <?xml version="1.0" encoding="UTF-16"?>
                    <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                      <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT10S</Delay></LogonTrigger></Triggers>
                      <Principals><Principal id="Author"><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
                      <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Enabled>true</Enabled></Settings>
                      <Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
                    </Task>
                    """;
                xmlPath = Path.Combine(Path.GetTempPath(), "HotspotLimiter-task-" + Guid.NewGuid().ToString("N") + ".xml");
                File.WriteAllText(xmlPath, xml, Encoding.Unicode);
                foreach (var arg in new[] { "/create", "/tn", TaskName, "/xml", xmlPath, "/f" }) info.ArgumentList.Add(arg);
            }
            else foreach (var arg in new[] { "/delete", "/tn", TaskName, "/f" }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new IOException("Unable to start Task Scheduler.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Task Scheduler timed out."); }
            if (process.ExitCode != 0 && enabled) throw new IOException(output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
            // A Run entry cannot elevate and also launched a duplicate instance of the scheduled task.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("HotspotLimiter", false);
        }
        catch (Exception ex) { Log.Write("Startup registration: " + ex); MessageBox.Show(ex.Message, "Windows startup registration failed"); }
        finally { if (xmlPath != null) { try { File.Delete(xmlPath); } catch { } } }
    }
}

public sealed class AppSettings
{
    public double LimitMbps { get; set; } = 4.0;
    public double DailyLimitGb { get; set; } = 0.0;
    public bool AutoStartHotspot { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
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
    static readonly System.Threading.Timer SaveTimer = new(_ => FlushIfDirty(), null, 15000, 15000);
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

    static void FlushIfDirty()
    {
        lock (Gate)
        {
            EnsureCurrentDayLocked();
            if (unsavedBytes > 0) SaveLocked();
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
            if (File.Exists(filePath) && new FileInfo(filePath).Length > 2_000_000) File.Move(filePath, filePath + ".previous", true);
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
                if (File.Exists(filePath) && new FileInfo(filePath).Length > 2_000_000) File.Move(filePath, filePath + ".previous", true);
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
