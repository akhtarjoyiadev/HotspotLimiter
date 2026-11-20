using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace HotspotLimiter;

static class SelfTests
{
    public static int VerifyDriver()
    {
        var results = new StringBuilder();
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up && n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase));
            if (nic == null) throw new InvalidOperationException("No active Wi-Fi Direct adapter; enable the hotspot first.");
            var address = nic.GetIPProperties().UnicastAddresses.First(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            var subnet = new HotspotSubnet(address.Address, address.IPv4Mask);
            // Sniff + receive-only: validate driver, signature, permission and real filter without intercepting/blocking traffic.
            var handle = WinDivert.Open(subnet.CaptureFilter, WinDivertLayer.NetworkForward, 0, 5);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new InvalidOperationException("WinDivertOpen error: " + Marshal.GetLastWin32Error());
            WinDivert.Close(handle);
            results.AppendLine("PASS signed WinDivert driver opened in non-blocking sniff/receive-only mode.");
            results.AppendLine("PASS real hotspot subnet filter: " + subnet.CaptureFilter);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "driver-check-results.txt"), results.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            results.AppendLine("FAIL " + ex);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "driver-check-results.txt"), results.ToString());
            return 1;
        }
    }

    public static int Run()
    {
        var results = new StringBuilder();
        int count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            count++; results.AppendLine("PASS " + name);
        }
        try
        {
            Check(Marshal.SizeOf<WinDivertAddress>() == 80, "WinDivert native address ABI is 80 bytes");
            var subnet = new HotspotSubnet(IPAddress.Parse("10.42.0.1"), IPAddress.Parse("255.255.254.0"));
            Check(subnet.IsClient(new byte[] { 10, 42, 1, 200 }), "custom subnet /23 is supported");
            Check(!subnet.IsClient(new byte[] { 10, 42, 0, 1 }), "host is not a client");
            Check(!subnet.IsClient(new byte[] { 10, 42, 0, 0 }), "network is not a client");
            Check(!subnet.IsClient(new byte[] { 10, 42, 1, 255 }), "broadcast is not a client");
            Check(!subnet.IsClient(new byte[] { 10, 42, 2, 1 }), "outside subnet passes untouched");
            var packet = new byte[1500]; packet[0] = 0x45;
            new byte[] { 10, 42, 1, 10 }.CopyTo(packet, 12);
            new byte[] { 8, 8, 8, 8 }.CopyTo(packet, 16);
            Check(subnet.TryClassify(packet, packet.Length, out var ip, out var download) && ip == "10.42.1.10" && !download, "upload classification");
            new byte[] { 8, 8, 8, 8 }.CopyTo(packet, 12);
            new byte[] { 10, 42, 1, 10 }.CopyTo(packet, 16);
            Check(subnet.TryClassify(packet, packet.Length, out ip, out download) && download, "download classification");
            Check(!subnet.TryClassify(packet, 12, out _, out _), "truncated packet passes safely");
            packet[0] = 0x41;
            Check(!subnet.TryClassify(packet, packet.Length, out _, out _), "invalid IPv4 header passes safely");
            packet[0] = 0x60;
            Check(!subnet.TryClassify(packet, packet.Length, out _, out _), "IPv6 is not misclassified as IPv4");
            foreach (double mbps in new[] { 0.5, 1, 2, 4, 100, 100000 })
            {
                var bucket = new TokenBucket(mbps * 1_000_000 / 8);
                Check(bucket.Reserve(65535, 0) == 0, $"full-size packet fits at {mbps} Mbps");
                double wait = bucket.Reserve(65535, 0);
                Check(double.IsFinite(wait) && wait >= 0, $"bounded wait at {mbps} Mbps");
                Check(bucket.Reserve(65535, wait + 0.000001) == 0, $"large packet progresses at {mbps} Mbps");
            }
            var low = new TokenBucket(62500);
            low.Reserve(65535, 0);
            double debt = low.Reserve(1500, 0);
            Check(Math.Abs(debt - 1500.0 / 62500) < 0.000001, "aggregate rate is enforced after burst");
            Check(!new HotspotState { State = "On", Success = false }.IsOn, "failed query is not mistaken for On");
            Check(new HotspotState { State = "On", Success = true }.IsOn, "verified On state");
            results.AppendLine($"PASS: {count} checks");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), results.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            results.AppendLine("FAIL: " + ex);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test-results.txt"), results.ToString());
            return 1;
        }
    }
}
