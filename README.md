# HotspotLimiter 🚀 | Free Windows Mobile Hotspot Bandwidth Limiter

**HotspotLimiter** is a lightweight, open-source Windows utility designed to monitor, shape, and throttle bandwidth across all devices connected to a **Windows Mobile Hotspot** (Internet Connection Sharing / Wi-Fi Direct). If you are looking for a free NetLimiter alternative or a Wi-Fi tethering speed capper for Windows 10 and Windows 11, this is the perfect tool.

Unlike traditional network limiters that throttle the entire PC connection, HotspotLimiter operates at the network packet forwarding layer—capping only traffic routed between connected clients and the internet, leaving your host PC's traffic completely unthrottled.

**Download the latest [True Single-File Executable](https://github.com/akhtarjoyiadev/HotspotLimiter/releases) (No installation required!)**

---

## ✨ Features

- **🌐 Aggregate Hotspot Bandwidth Cap**: Configurable aggregate bandwidth limit (upload + download) enforced using a precise token-bucket algorithm.
- **📅 Per-Device Daily Quotas**: Optional persistent daily data limit for each hotspot device; packets are dropped after the device reaches its quota.
- **⚡ Host PC Unaffected**: Uses WinDivert's `NETWORK_FORWARD` layer so local PC downloads/uploads remain at full network speed.
- **📱 Live Device & Traffic Tracker**:
  - Enumerates associated hotspot clients through Windows, including clients that have not sent internet traffic.
  - Classifies forwarded IPv4 traffic using the actual hotspot adapter address and subnet mask.
  - Resolves client MAC addresses via ARP (`SendARP`).
  - Resolves device hostnames via DNS (`GetHostEntry`).
  - Live per-device download/upload stats and real-time bandwidth consumption.
- **📶 Auto-Detect & Hotspot Control**:
  - Automatically manages Windows Mobile Hotspot state via Windows Runtime (`NetworkOperatorTetheringManager`) APIs.
  - Option to auto-start Mobile Hotspot if turned off.
  - Limiter auto-start is optional and disabled by default, so bandwidth and quota settings can be selected before starting.
- **🔔 System Tray Integration**: Minimizes to system tray on close with background monitoring.
- **🚀 Windows Auto-Start**: Supports elevated startup with Windows login via Task Scheduler with a 10-second boot delay for network adapter readiness.

---

## 📋 Requirements

- **Operating System**: 64-bit Windows 10 or Windows 11 (x64).
- **Permissions**: **Must be run as Administrator** (required for WinDivert kernel-mode packet filtering).
- **Wi-Fi Hardware**: Wi-Fi adapter and driver supporting Windows Mobile Hotspot / Wi-Fi Direct. Legacy `Hosted network supported` alone does not establish Mobile Hotspot compatibility.
- **WinDivert Native Files**:
  - `WinDivert.dll` (x64)
  - `WinDivert64.sys` (x64 signed kernel driver)
  *(Download from [WinDivert 2.2.2 Official Releases](https://github.com/basil00/WinDivert/releases/tag/v2.2.2))*.

---

## 🛠️ Building from Source

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### 1. Build Standalone Single-File Release (No .NET installation required on target PC)
The project is configured to embed `WinDivert.dll` and `WinDivert64.sys` automatically as embedded resources. They will be extracted at runtime, so you only need to distribute the single `HotspotLimiter.exe` file.

```powershell
dotnet publish HotspotLimiter.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o "./publish"
```

### 2. Run
Right-click the generated `HotspotLimiter.exe` and select **Run as Administrator**.

---

## 🏗️ Architecture & How It Works

1. **Packet Capture**: Intercepts forwarded packets using WinDivert's `NETWORK_FORWARD` layer:
   ```csharp
   handle = WinDivert.Open(subnet.CaptureFilter, WinDivertLayer.NetworkForward, 0, 0);
   ```
2. **Token Bucket Rate Limiting**: Releases packets at the configured aggregate upload + download rate. The burst capacity is at least one full 65,535-byte IP packet, preventing permanent stalls at low caps. Only the hotspot IPv4 subnet is intercepted; unrelated forwarding is left alone. IPv6 is currently not shaped or quota-accounted.
3. **Per-Device Accounting**: Inspects IPv4 headers to categorize client traffic into download vs. upload and calculates instantaneous data rates.
4. **Daily Quota Enforcement**: Stores the current day's per-device byte totals in `%LOCALAPPDATA%\HotspotLimiter\daily-usage.json` and blocks new forwarded packets after the configured device quota is reached. Settings are stored in `%LOCALAPPDATA%\HotspotLimiter\settings.json`.

---

## Reliability update (November 2026)

- One serialized hotspot controller; no competing startup task and timer operations.
- Actual Windows tethering state, client count, and useful operation errors instead of treating a leftover private IP as proof the access point is on.
- 2.4 GHz compatibility configuration is awaited before starting the access point; startup retries use backoff.
- Manual hotspot Stop is respected, even with auto-start checked.
- No automatic `hosts.ics` rewriting or ICS service restarts at app startup.
- Explicit **Repair hotspot** retains the SSID/password, briefly restarts the AP, and keeps the limiter off for connectivity testing.
- Packet worker faults are surfaced and the diversion handle is closed safely rather than silently leaving traffic blocked.
- Quota persistence is periodic instead of writing JSON every megabyte on the packet path. Quotas remain keyed by IPv4 address, not a tamper-proof device identity; DHCP reassignment or MAC randomization can affect attribution.
- New installations default to no daily quota (`0` = off). Existing saved settings are preserved.
- Elevated application manifest, a single Task Scheduler startup entry, log rotation, and a local diagnostics report (no Wi-Fi passwords).

### Build and regression checks

```powershell
.\build.ps1
# Non-elevated, using the built DLL to bypass the executable's elevation manifest:
dotnet .\bin\Release\net8.0-windows\HotspotLimiter.dll --self-test
# Elevated; verifies the actual subnet filter and signed driver in sniff/receive-only mode:
.\publish-reliable\HotspotLimiter.exe --verify-driver
```

Tests write `self-test-results.txt` and `driver-check-results.txt` beside the executable. A successful sniff-mode check does not establish end-to-end device connectivity or measured cap accuracy.

### Troubleshooting devices that cannot join

1. Turn the limiter off, then use **Repair hotspot**. Reconnect a phone and verify internet access before enabling a bandwidth cap or quota.
2. If a phone still rejects the network, forget the saved network on that phone and reconnect with the password shown in Windows Settings → Network & internet → Mobile hotspot.
3. Use **Diagnostics** to check the Windows state, client limit, actual adapter, and driver. A client with no IPv4 address indicates an address-assignment problem, not a bandwidth setting.
4. If association fails with the limiter off, investigate the Wi-Fi driver, USB stability/power management, and Windows sharing configuration. Do not disable the firewall or perform a blanket network reset.

On the inspected machine, the Ralink RT5370 (`USB VID_148F / PID_5370`) adapter uses Microsoft driver 5.1.22.0 dated April 2015. Windows logged driver disconnects while associating to its upstream Wi-Fi profile, while Ethernet supplied internet. This is evidence of a driver/radio issue, not proof of the cause of phone-side rejection. If problems persist, use a trusted compatible driver or a modern Windows 11-compatible Wi-Fi adapter. Avoid random driver download sites.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
WinDivert is licensed under the LGPL / GPL. See [WinDivert License](https://github.com/basil00/WinDivert) for details.
