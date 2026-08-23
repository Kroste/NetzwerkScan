# NetScanner

**LAN discovery · port scanning · device fingerprinting · ONVIF/RTSP camera detection with embedded live video — plus an interactive network map.**

A lean desktop tool that makes your own network visible: which devices are online, what they are (router, printer, NAS, camera, phone …), which services they offer — and how the network is exposed to the outside. Cross-platform for **Windows** and **Linux**, with the interface available in **English and German**.

> .NET 10 (C# latest) · Avalonia 12 · LibVLCSharp (Core) · NLog · MVVM (CommunityToolkit)

![NetScanner main window](docs/screenshots/01-hauptfenster.png)

---

## Contents

- [What NetScanner does](#what-netscanner-does)
- [Installation](#installation)
- [Manual](#manual)
  - [1. The main window](#1-the-main-window)
  - [2. Configuring and starting a scan](#2-configuring-and-starting-a-scan)
  - [3. Reading the result list](#3-reading-the-result-list)
  - [4. Detail panel](#4-detail-panel)
  - [5. Actions from the context menu](#5-actions-from-the-context-menu)
  - [6. Watching a camera stream](#6-watching-a-camera-stream)
  - [7. Exporting results](#7-exporting-results)
  - [8. Network map and path to the outside](#8-network-map-and-path-to-the-outside)
- [Weak-spot audit](#weak-spot-audit)
- [Password leak check](#password-leak-check)
- [Exposure check](#exposure-check)
- [Settings](#settings)
- [Updates](#updates)
- [System tray](#system-tray)
- [How device detection works](#how-device-detection-works)
- [Platform notes](#platform-notes)
- [Logging](#logging)
- [Project layout](#project-layout)
- [Deliberate limits](#deliberate-limits)
- [Licence](#licence)

---

## What NetScanner does

| Feature | Description |
|---|---|
| **Discovery** | ICMP ping sweep across a CIDR range, complemented by the ARP/neighbour table. Devices that stay silent on ping (phones in doze) but show up via ARP, mDNS, SSDP or ONVIF are listed too. |
| **Port scan** | Asynchronous TCP connect scan, throttled with `SemaphoreSlim`. Either a curated set of common ports or the full 1–65535 range. |
| **Device and OS fingerprinting** | Estimates device type (router, printer, NAS, camera, mobile …) and OS family from TTL, open ports, MAC vendor (OUI) and banners. |
| **Name resolution** | Reverse DNS, mDNS/Bonjour, NetBIOS and SSDP/UPnP — the best available name is shown. |
| **Camera detection** | ONVIF WS-Discovery + port heuristics (554/8554) + an RTSP `OPTIONS` probe. Optionally with RTSP credentials for the stream URL. |
| **Live video** | RTSP stream embedded directly in the window via LibVLC (`NativeControlHost`). |
| **Weak-spot audit** *(opt-in)* | Checks every device for common **factory logins** (default credentials) across four surfaces — RTSP streams, HTTP Basic/Digest web logins, **Telnet** and **FTP** — plus **open** streams, **anonymous FTP** and passwordless Telnet. On a hit the device is flagged and — for a camera — the picture is shown straight away. Your own network only. |
| **Password leak check** | Uses *Have I Been Pwned* (k-anonymity) to check whether a device password appears in known data leaks — **without transmitting the password** — plus a local strength and crack-time estimate against fast (MD5) and slow (bcrypt) hashes. |
| **Exposure check** | Asks the router (UPnP IGD) for active **port forwardings** and the public IP — showing what can be reached from the internet and whether a forwarding points at a camera. Includes a Shodan self-check. |
| **Host actions** | Per device: open the web interface, SSH, RDP, SMB share, copy IP/MAC/name, **Wake-on-LAN**. |
| **Export** | The host list as **CSV** (semicolon separated, for German Excel) or **JSON**. |
| **Network map** | Interactive star topology: gateway at the centre, devices around it, coloured by type. |
| **Path to the outside (traceroute)** | Traces the route from the gateway towards the internet — ICMP with increasing TTL, no external tool. |
| **Two languages** | The whole interface in English and German, switchable live without a restart. |
| **Self-update** | Checks GitHub releases in the background and installs the update on request. |
| **Audit logging** | Two separate logs (everything / user input only). Passwords are never logged. |

---

## Installation

### Prebuilt releases (recommended)

The [releases](../../releases) page carries prebuilt packages produced by the GitHub Actions workflow:

- **Windows (recommended):** `NetScanner-…-Setup.exe` — installer with a start-menu entry. It **carries the .NET 10 Desktop Runtime** and installs it if needed; if it is already present, that step is skipped.
- **Windows (portable):** `NetScanner-…-win-x64.zip` — unpack and run `NetScanner.exe`. This package is framework-dependent and therefore requires an installed **.NET 10 Desktop Runtime** (otherwise the app will not start).
- **Linux:** `NetScanner-…-linux-x64.tar.gz` **or** `NetScanner-…-x86_64.AppImage`.

The embedded camera preview uses an existing **VLC installation** — libvlc is **not** bundled, which keeps the packages small. Scanning, port scanning, detection and the network map work without VLC as well; only the live video stays off and the app shows a hint instead. Details below.

### Building from source

```bash
git clone https://github.com/Kroste/NetzwerkScan.git
cd NetzwerkScan
dotnet restore
dotnet build
dotnet run --project NetScanner
```

Requirement: the **.NET 10 SDK** (pinned in `global.json`). Run the tests with:

```bash
dotnet test NetScanner.Tests/NetScanner.Tests.csproj
```

### libvlc / VLC (for the camera preview)

NetScanner no longer bundles libvlc itself — that saved roughly 85 MB per Windows package. Instead the app loads libvlc at runtime from an existing VLC installation. If it is missing, everything except the embedded video keeps working and the video area shows a hint with a download link.

- **Windows:** install [VLC media player](https://www.videolan.org/vlc/) in its **64-bit** variant (the default download). NetScanner looks for it under `%ProgramFiles%\VideoLAN\VLC`. A 32-bit VLC is deliberately ignored because it does not match the 64-bit app. For an installation elsewhere, point the environment variable `NETSCANNER_VLC_DIR` at the VLC directory (the one containing `libvlc.dll`).
- **Linux:** install the system libvlc. On immutable Fedora/Bazzite that belongs **inside the `dotnet10` distrobox container**, not on the host:
  ```bash
  sudo dnf install vlc-libs      # Fedora: provides libvlc.so plus plugins
  # Debian/Ubuntu:  sudo apt install libvlc-dev vlc-plugin-base
  ```
  `Core.Initialize()` then finds the system libvlc automatically.

---

## Manual

### 1. The main window

At the top sits the title bar with live counters (**hosts** / **cameras**), buttons for the **network map**, the **password leak check**, the **exposure check**, **settings** (⚙) and **about** (i). Below it the input area, on the left the scrollable result list, on the right the detail panel with the video area.

![Main window after a scan](docs/screenshots/01-hauptfenster.png)

### 2. Configuring and starting a scan

![Scan options](docs/screenshots/02-scan-optionen.png)

- **Network range (CIDR):** for example `192.168.68.0/24`. Your local subnet is preselected at startup.
- **ONVIF wait (ms):** how long to wait for ONVIF answers. Higher means more cameras found, but a slower scan.
- **All ports (1–65535):** off means a curated set of common ports (fast); on means a full scan (considerably slower).
- **Probe RTSP:** enables the RTSP `OPTIONS` probe used to confirm a camera.
- **RTSP login (optional):** user and password for cameras that require authentication for the stream URL. The password is **never** logged.

**Start scan** begins the run; results appear **as they stream in**, so there is no need to wait for the end. **Cancel** stops cleanly.

### 3. Reading the result list

Every device is a card. How to read one:

![Anatomy of a host card](docs/screenshots/03-host-karte.png)

- **IP and latency** at the top (`2 ms`, or ARP-only if the device does not answer ping).
- **Type badge** such as `Router · Linux/Unix` or `Web/IoT device · Windows` — the estimated combination of device type and OS.
- **Status dot:** green means reachable via ICMP, amber means seen via ARP only.
- **Discovery line:** plain text from mDNS/SSDP/NetBIOS, e.g. `UPnP: Router` or the services found.
- **Best name:** DNS > mDNS > NetBIOS name, where available.
- **Banner line:** e.g. `HTTP: Microsoft-IIS/10.0` or the SSH banner.
- **Port chips:** open ports with service names (`80 HTTP`, `443 HTTPS`, `139 NetBIOS`, `445 SMB` …).
- **MAC address** at the bottom, including detection of randomised MACs (→ mobile device).

### 4. Detail panel

Clicking a card shows all the information gathered on the right, together with the matching **action buttons**. For an active camera the video appears below.

![Detail panel](docs/screenshots/04-detail-panel.png)

### 5. Actions from the context menu

**Right-click** a card to open the context menu. It only shows what fits the device:

![Context menu](docs/screenshots/05-kontextmenu.png)

| Action | Visible when | Behaviour |
|---|---|---|
| **Open in browser** | port 80/443/8080/8443 open | Opens the web interface (HTTPS preferred) |
| **SSH** | port 22 open | Windows: starts `ssh`; Linux/macOS: copies the command |
| **RDP** | port 3389 open | Windows: `mstsc`; hidden elsewhere |
| **File share (SMB)** | port 139/445 open | Windows: `\\IP` in Explorer; otherwise `smb://IP` |
| **Copy IP / MAC / name** | always / when available | To the clipboard |
| **Wake-on-LAN** | MAC known | Sends a magic packet (UDP broadcast) |

### 6. Watching a camera stream

For detected cameras the detail panel shows the **RTSP URL** and an **Open stream** button. The video plays embedded in the window.

![Camera with live video](docs/screenshots/06-kamera-stream.png)

> If a camera requires credentials for its stream, enter them under **RTSP login** before scanning. NetScanner never guesses passwords here.

### 7. Exporting results

The export buttons save the complete host list:

- **CSV** — semicolon separated, suitable for German Excel.
- **JSON** — structured, for further processing or scripts.

### 8. Network map and path to the outside

The node button in the header opens the **network map** in its own window (non-modal, so you can keep scanning in parallel).

![Network map](docs/screenshots/07-netzwerkkarte.png)

- **Star topology:** the **gateway** at the centre (taken from the default gateway, falling back to the router detected via UPnP), every other device around it.
- **Colour equals device type** (see the legend at the top right).
- **Clicking a node** selects the device — the detail panel in the main window follows immediately.
- **Refresh** redraws the map from the current scan.

At the bottom is the **path to the outside (traceroute)** bar: enter a target (default `8.8.8.8`, empty means the gateway) and choose **Trace path** — the hops appear live as a chain "This PC → … → target".

![Traceroute hop chain](docs/screenshots/08-traceroute.png)

> On a flat LAN the way to the gateway is exactly **one hop** — switches and access points are invisible at the IP level. The chain gets interesting with an internet target or across subnet boundaries.

---

## Weak-spot audit

Optionally (the **Check for weak spots** checkbox) NetScanner tests **every** device for the four most common weak spots in a home network. Each is only probed when the relevant port is open, so a device is never hammered for services it does not run:

- **Open RTSP stream** *(cameras)* — the stream answers without credentials. The camera is flagged and the preview opens automatically.
- **RTSP factory login** *(cameras)* — a factory account works against the authenticated stream; the preview then plays with those credentials.
- **Web login** *(any device with a web interface, ports 80/443/8080/8443/…)* — factory credentials against HTTP basic/digest. This is where a NAS, printer, PDU, switch or IoT hub with `admin/admin` gets caught. Form-based logins (status 200 instead of 401) are left alone — only basic and digest.
- **Telnet** *(port 23)* — the classic IoT/Mirai weak spot: a passwordless shell, or a factory login such as `root/root`. Reported conservatively, only when the response clearly shows a shell and no re-prompt or error.
- **FTP** *(port 21)* — anonymous access, or a factory login. Read-only (only `USER`/`PASS`/`QUIT`), nothing is transferred or changed.

The credential list is a fixed, short set of documented factory logins (`admin/admin`, `admin/12345`, `root/root`, `supervisor/supervisor`, …) — this is **not brute force**. A hit shows a red warning badge with the login found; that login is shown in the interface and **never written to the log** (only the fact of the hit is logged, with the user name at most).

> ⚠ **Your own network only.** Default-credential checks against systems you do not own, without explicit permission, can have legal consequences (in Germany §202c StGB among others). The feature is therefore off by default and has to be enabled deliberately. Use it only to secure your own devices.

---

## Password leak check

The shield button at the top opens the password leak check. It answers one question: *does the password of my camera or my router appear in known data leaks?* — without you having to store it anywhere.

Technically this uses the **k-anonymity** scheme of the Pwned Passwords database from *Have I Been Pwned* — the same one Bitwarden, 1Password, Google and Firefox use for their leak warnings:

1. NetScanner computes the SHA-1 hash of the password **locally**.
2. Only the **first 5 characters** of that hash go to the API.
3. The API returns every hash suffix for that prefix (several hundred).
4. The comparison of whether your password is among them happens **locally**.

The password and the full hash never leave your device — the API cannot tell which password was checked. The `Add-Padding` header additionally normalises the response size, so no conclusion can be drawn from that either. Unlike storing it in a password manager, the password stays entirely with you.

This is deliberately **not brute force** against the device: you check your own, known password as a string against a leak database — no system is accessed.

NetScanner also estimates the password strength **locally** (offline, nothing transmitted) and translates it into an offline crack time against a fast hash (MD5 and friends, roughly 100 billion attempts per second) and a slow one (bcrypt). That answers the question the leak check leaves open: how long does a password that is *not* leaked hold up against plain brute force? On older devices that store MD5 hashes this is the number that matters — there, length and randomness decide, not list hits.

---

## Exposure check

The globe button answers the most important question for a camera on a home network: *can it be reached from outside at all?* The notorious "open webcam" sites list cameras that are **exposed** and run with factory credentials or none — not ones whose strong password was broken.

To find out, NetScanner asks your router via **UPnP IGD** (`GetGenericPortMappingEntry`) for its active **port forwardings** and matches each against the scan results. You then see it in black and white: "port :554 → 192.168.x.y (camera)" means that camera stream is reachable from the internet. The **public IP** is determined as well, with a direct link to check it on **Shodan**. The query is strictly read-only — no forwarding is created or changed.

Three things this check cannot do for you, but that belong to securing a camera: **vendor cloud and P2P access** bypasses UPnP entirely (switch it off in the camera app itself), **UPnP** on the router should be disabled if you do not need it, and the safest remote access is a **VPN into the home network** (WireGuard on a Raspberry Pi, for example) rather than a camera exposed to the internet — combined with a **separate VLAN** without internet access for the camera.

---

## Settings

The gear button opens the settings window. Currently it holds the **display language** (English or German). The change takes effect immediately in every open window — no restart needed — and is remembered.

Settings live in `%AppData%\NetScanner\settings.json` (Windows) or `~/.config/NetScanner/settings.json` (Linux), deliberately not next to the executable, which may sit in a read-only location. The file is written atomically; if it ever becomes unreadable JSON it is moved aside as `settings.json.broken` and the app starts with defaults instead of overwriting it.

---

## Updates

NetScanner checks the GitHub releases in the background at startup. This never blocks the window and a failed check (offline, proxy, rate limit) only produces a log warning, never a dialog.

If a newer version exists, an **⬇ Update** badge appears in the title bar. It opens the about window, where **Install update** downloads the matching package for your platform, starts the platform-specific installer script and closes NetScanner so it can be replaced. The app restarts on its own afterwards.

You can always trigger the check yourself with **Check for updates** in the about window.

---

## System tray

Minimising puts NetScanner into the system tray instead of the taskbar — useful during a long scan. A click on the tray icon (or **Show** in its menu) brings the window back, **Quit** ends the app. Closing the window with ✕ still quits normally.

On a headless system or with a broken DBus session the tray is skipped and minimising behaves as usual.

---

## How device detection works

NetScanner combines several signals into one estimate — deliberately without raw sockets, so **no elevated privileges** are needed:

| Signal | Source | Example |
|---|---|---|
| **TTL** | ICMP reply | ≤64 → Linux/Unix/Android, ≤128 → Windows, higher → network device |
| **Open ports** | TCP connect | 3389 → Windows, 22 → Linux, 9100/515/631 → printer, 5000/5001 → NAS, 62078 → iPhone |
| **MAC vendor** | OUI lookup | Vendor from the first 3 MAC octets; a set locally-administered bit means a randomised (mobile) MAC |
| **mDNS/Bonjour** | multicast 224.0.0.251:5353 | Chromecast, AirPlay, printer, Sonos, HomeKit … |
| **NetBIOS** | UDP 137 | Windows/Samba name plus workgroup |
| **SSDP/UPnP** | multicast 239.255.255.250:1900 | Router, media server, smart TV (from the `SERVER` header) |
| **Banner** | HTTP `HEAD` / SSH handshake | `Microsoft-IIS/10.0`, `nginx`, `OpenSSH_9.6` |

The UPnP type and mDNS services carry the highest confidence; TTL, ports and OUI round out the picture.

---

## Platform notes

- **Avalonia 12 and video:** `LibVLCSharp.Avalonia` is deliberately **not** used — it is tied to Avalonia 11. Instead `NativeVideoView` hands LibVLC the native window handle directly. Once the official package catches up with 12, `NativeVideoView` can be swapped for it.
- **Wayland (KDE Plasma on Bazzite):** native embedding is most stable under **X11/XWayland**. Avalonia uses the X11 backend by default on Linux, so the `XID` handle works under Wayland too. If the video stays black, start the app with X11 forced.
- **ONVIF multicast and firewalls:** WS-Discovery needs outgoing UDP multicast on port 3702. Restrictive networks may block it — the port heuristic (554/8554) still applies.
- **Traceroute on Linux:** hop IPs are reliable on **Windows**. On Linux the unprivileged ICMP socket does not always return the address of the router that answered on TTL expiry — then `* * *` appears instead of the hop IP. The star map itself is unaffected.
- **Multiple interfaces:** the sweep and WS-Discovery run per active IPv4 interface.

---

## Logging

Configured in `NetScanner/nlog.config` (copied next to the executable, `autoReload`).
Target folder: `%AppData%\NetScanner\logs` (Windows) or `~/.config/NetScanner/logs` (Linux).

- `netscanner-<date>.log` — **every step** (debug and up).
- `userinput-<date>.log` — **user input only** (the `UserInput` logger): scan start with parameters, cancellation, opening a stream, field changes.
- **Passwords are never logged.** A masking layout renderer strips password, token, secret and API-key assignments, credentials embedded in URLs (`rtsp://user:pass@host`) and authorization headers from every log target — including the factory login found by the weak-spot audit, of which only the user name is recorded.

---

## Project layout

Flat structure, no `src/` folder: the app in `NetScanner/`, the tests in `NetScanner.Tests/`, package versions centrally in `Directory.Packages.props`, the version from the git tag via MinVer.

| Layer | File(s) | Purpose |
|---|---|---|
| Discovery | `Services/NetworkScanner.cs` | ICMP ping sweep (no raw socket), streaming |
| ARP/OUI | `Services/ArpResolver.cs`, `Services/OuiLookup.cs` | MAC resolution and vendor detection |
| Port scan | `Services/PortScanner.cs` | async TCP connect, throttled via `SemaphoreSlim` |
| Fingerprinting | `Services/DeviceClassifier.cs`, `Services/BannerGrabber.cs` | type and OS from TTL, ports, OUI, banners |
| Discovery protocols | `Services/MdnsDiscovery.cs`, `Services/NetBiosProbe.cs`, `Services/SsdpDiscovery.cs` | mDNS / NetBIOS / SSDP |
| ONVIF/RTSP | `Services/OnvifDiscovery.cs`, `Services/RtspProbe.cs` | WS-Discovery plus RTSP probe |
| Wake-on-LAN | `Services/WolSender.cs` | magic packet (UDP broadcast) |
| Traceroute | `Services/TracerouteService.cs` | ICMP with increasing TTL |
| Orchestration | `Services/ScanOrchestrator.cs` | ties everything together, classifies cameras |
| Updates | `Services/UpdateService.cs` | GitHub release check plus self-update |
| Settings | `Config/AppSettings.cs`, `Config/JsonFileStore.cs` | atomic JSON persistence |
| Localization | `Localization/` | `LocalizationService`, `LocalizedString`, `{loc:Tr}` markup, resx |
| Video | `Controls/NativeVideoView.cs` | LibVLC in Avalonia 12 via `NativeControlHost` |
| Chrome | `Views/ChromeWindow.cs`, `Controls/TitleBar.axaml` | custom window chrome shared by every window |
| UI/state | `ViewModels/MainViewModel.cs`, `Views/*.axaml` | MVVM, audit logging, network map |

---

## Deliberate limits

- **TCP connect scan instead of SYN scan** — no raw socket, therefore no elevated privileges (and no nmap-grade OS fingerprinting).
- **No password guessing.** You supply ONVIF `GetStreamUri` and RTSP credentials yourself — this is meant for devices on **your own** network. The weak-spot audit tries a short, documented list of factory logins across RTSP, HTTP, Telnet and FTP and nothing else; there are no word lists and no brute force, and there never will be.
- **Traceroute shows no LAN topology**, only the path outwards — within a single subnet there are no hops.

---

## Licence

MIT — see [LICENSE](LICENSE).

*NetScanner is a private tool for taking inventory of your own network. Only scan networks you are authorised to scan.*
