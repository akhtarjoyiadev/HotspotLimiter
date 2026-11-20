using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace HotspotLimiter;

public sealed class ConnectedClient
{
    public string IpAddress { get; set; } = "";
    public string MacAddress { get; set; } = "";
}

public sealed class HotspotState
{
    public string State { get; set; } = "Unknown";
    public bool Success { get; set; }
    public string Error { get; set; } = "";
    public int ClientCount { get; set; }
    public int MaxClientCount { get; set; }
    public string Profile { get; set; } = "";
    public string Band { get; set; } = "";
    public string Ssid { get; set; } = "";
    public ConnectedClient[] Clients { get; set; } = Array.Empty<ConnectedClient>();
    public bool IsOn => Success && State == "On";
}

public static class HotspotController
{
    static readonly SemaphoreSlim Operations = new(1, 1);
    public static HotspotState LastState { get; private set; } = new();
    public static async Task<bool> IsHotspotOnAsync() => (await ExecuteAsync("status")).IsOn;
    public static async Task<bool> StartHotspotAsync() => (await ExecuteAsync("start")).IsOn;
    public static async Task<bool> StopHotspotAsync()
    {
        var state = await ExecuteAsync("stop");
        return state.Success && state.State == "Off";
    }
    public static async Task<bool> RepairAsync() => (await ExecuteAsync("repair")).IsOn;

    static async Task<HotspotState> ExecuteAsync(string action)
    {
        await Operations.WaitAsync();
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("HotspotLimiter.Hotspot.ps1")
                ?? throw new InvalidOperationException("Missing hotspot controller resource.");
            using var reader = new StreamReader(resource);
            string script = "& {\n" + await reader.ReadToEndAsync() + "\n} -Action '" + action + "'";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Unable to start Windows hotspot controller.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(95));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Windows hotspot controller timed out. Check the Wi-Fi driver.");
            }
            string output = (await outputTask).Trim();
            string error = (await errorTask).Trim();
            var state = JsonSerializer.Deserialize<HotspotState>(output)
                ?? throw new InvalidOperationException("Windows returned an empty hotspot state. " + error);
            LastState = state;
            if (action != "status" || !state.Success)
                Log.Write($"Hotspot {action}: state={state.State}, clients={state.ClientCount}/{state.MaxClientCount}, error={state.Error}");
            return state;
        }
        catch (Exception ex)
        {
            LastState = new HotspotState { Error = ex.Message };
            Log.Write("Hotspot controller: " + ex.Message);
            return LastState;
        }
        finally { Operations.Release(); }
    }
}

// Classification uses the actual private adapter's address and mask, not a fixed /24.
public sealed class HotspotSubnet
{
    readonly uint host;
    readonly uint mask;
    readonly uint network;
    readonly uint broadcast;
    static uint Value(ReadOnlySpan<byte> b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    public HotspotSubnet(IPAddress address, IPAddress netmask)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || netmask.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("The hotspot requires an IPv4 address and subnet mask.");
        host = Value(address.GetAddressBytes()); mask = Value(netmask.GetAddressBytes());
        network = host & mask; broadcast = network | ~mask;
        if (mask == 0 || mask == uint.MaxValue) throw new ArgumentException("Invalid hotspot subnet mask.");
    }
    public bool IsClient(ReadOnlySpan<byte> bytes)
    {
        uint ip = Value(bytes);
        return (ip & mask) == network && ip != host && ip != network && ip != broadcast;
    }
    static string Format(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";
    public string CaptureFilter => $"ip and (ip.SrcAddr >= {Format(network + 1)} and ip.SrcAddr < {Format(broadcast)} or ip.DstAddr >= {Format(network + 1)} and ip.DstAddr < {Format(broadcast)})";
    public bool TryClassify(byte[] packet, int length, out string? client, out bool download)
    {
        client = null; download = false;
        if (length < 20 || packet.Length < length || packet[0] >> 4 != 4) return false;
        int headerLength = (packet[0] & 15) * 4;
        if (headerLength < 20 || headerLength > length) return false;
        if (IsClient(packet.AsSpan(12, 4))) client = new IPAddress(packet.AsSpan(12, 4)).ToString();
        else if (IsClient(packet.AsSpan(16, 4))) { client = new IPAddress(packet.AsSpan(16, 4)).ToString(); download = true; }
        return client != null;
    }
}

public sealed class TokenBucket
{
    readonly double rate;
    readonly double capacity;
    double tokens;
    public TokenBucket(double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        rate = bytesPerSecond;
        // A full IP packet must always fit; otherwise low limits deadlock on large/offloaded packets.
        capacity = Math.Max(65535, rate * 0.1);
        tokens = capacity;
    }
    public double Reserve(int bytes, double elapsedSeconds)
    {
        if (bytes < 0 || bytes > 65535) throw new ArgumentOutOfRangeException(nameof(bytes));
        tokens = Math.Min(capacity, tokens + Math.Max(0, elapsedSeconds) * rate);
        if (tokens >= bytes) { tokens -= bytes; return 0; }
        return (bytes - tokens) / rate;
    }
}

static class Diagnostics
{
    public static async Task<string> WriteReportAsync()
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HotspotLimiter", "diagnostics");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"hotspot-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        var state = HotspotController.LastState;
        var report = new StringBuilder();
        report.AppendLine($"HotspotLimiter diagnostics {DateTime.Now:O}");
        report.AppendLine($"OS: {Environment.OSVersion}; Elevated: {new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator)}");
        report.AppendLine(JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        report.AppendLine("No Wi-Fi passwords are collected. Network names, IPs and MACs may appear below.");
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            report.AppendLine($"\n{nic.Name} | {nic.Description} | {nic.OperationalStatus}");
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
                report.AppendLine($"  {address.Address}/{address.PrefixLength}");
        }
        foreach (string arguments in new[] { "wlan show drivers", "wlan show interfaces" })
        {
            using var p = Process.Start(new ProcessStartInfo("netsh.exe", arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) continue;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(10000);
            try { await p.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { p.Kill(true); await p.WaitForExitAsync(); }
            report.AppendLine("\nnetsh " + arguments); report.AppendLine(await stdout); report.AppendLine(await stderr);
        }
        await File.WriteAllTextAsync(path, report.ToString());
        return path;
    }
}
