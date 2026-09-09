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
  - Auto-discovers connected hotspot client devices (`192.168.137.x` subnet).
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
- **Wi-Fi Hardware**: Wi-Fi adapter supporting Hosted Network / Wi-Fi Direct (Windows Mobile Hotspot capability).
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
   handle = WinDivert.Open("ifIdx == <virtualAdapterIdx> || ifIdx == <uplinkIdx>", WinDivertLayer.NetworkForward, 0, 0);
   ```
2. **Token Bucket Rate Limiting**: Buffers and releases packets matching the specified token rate (e.g. 5.0 Mbps cap) with a small burst reserve (0.25s) for smooth streaming and browsing latency.
3. **Per-Device Accounting**: Inspects IPv4 headers to categorize client traffic into download vs. upload and calculates instantaneous data rates.
4. **Daily Quota Enforcement**: Stores the current day's per-device byte totals in `%LOCALAPPDATA%\HotspotLimiter\daily-usage.json` and blocks new forwarded packets after the configured device quota is reached. Settings are stored in `%LOCALAPPDATA%\HotspotLimiter\settings.json`.

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
WinDivert is licensed under the LGPL / GPL. See [WinDivert License](https://github.com/basil00/WinDivert) for details.
