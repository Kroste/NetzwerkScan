# NetScanner

LAN-Discovery + Portscan + ONVIF/RTSP-Kameraerkennung mit eingebettetem Live-Video.
**.NET 10 (C# 14) · Avalonia 12 · LibVLCSharp (Core) · NLog · MVVM (CommunityToolkit).**

## Aufbau

| Schicht | Datei(en) | Aufgabe |
|---|---|---|
| Discovery | `Services/NetworkScanner.cs` | ICMP-Ping-Sweep (kein Raw-Socket), streamend |
| Portscan | `Services/PortScanner.cs` | async TCP-Connect, gedrosselt via `SemaphoreSlim` |
| ONVIF | `Services/OnvifDiscovery.cs` | WS-Discovery (Multicast 239.255.255.250:3702) |
| RTSP | `Services/RtspProbe.cs` | RTSP-`OPTIONS`-Probe + Hersteller-Pfadmuster |
| Orchestrierung | `Services/ScanOrchestrator.cs` | fuehrt alles zusammen, klassifiziert Kameras |
| Video | `Controls/NativeVideoView.cs` | LibVLC in Avalonia 12 via `NativeControlHost` |
| UI/State | `ViewModels/MainViewModel.cs`, `Views/MainWindow.axaml` | MVVM, Audit-Logging |

## Voraussetzungen

- **.NET 10 SDK** (>= 10.0.300).
- **libvlc** (Native-Bibliotheken):
  - **Windows:** kommt automatisch ueber das NuGet `VideoLAN.LibVLC.Windows`.
  - **Linux (Bazzite/Fedora, im `dotnet10`-Distrobox):**
    ```bash
    sudo dnf install vlc-libs    # liefert libvlc.so + Plugins
    ```
    Auf immutable Fedora gehoert das in den **Distrobox-Container**, nicht aufs Host-System.
    `Core.Initialize()` findet die System-libvlc dann automatisch.

## Build & Start

```bash
dotnet restore
dotnet run
```

## Plattform-Hinweise (wichtig)

- **Avalonia 12 + Video:** Es wird bewusst **nicht** `LibVLCSharp.Avalonia` verwendet
  (das haengt an Avalonia 11). Stattdessen reicht `NativeVideoView` LibVLC direkt das
  native Fenster-Handle. Sobald das offizielle Paket auf 12 nachzieht, kannst du
  `NativeVideoView` 1:1 dagegen tauschen.
- **Wayland (KDE Plasma auf Bazzite):** Native-Embedding laeuft am stabilsten unter
  **X11/XWayland**. Avalonia nutzt unter Linux standardmaessig den X11-Backend; das
  `XID`-Handle funktioniert dann auch unter Wayland via XWayland. Falls das Video
  schwarz bleibt, App testweise mit erzwungenem X11 starten.
- **ONVIF-Multicast & Firewall:** WS-Discovery braucht ausgehenden UDP-Multicast auf
  Port 3702. Auf restriktiven Netzen (LHP) kann das geblockt sein — die Port-Heuristik
  (554/8554) funktioniert dann weiterhin.
- **Mehrere Interfaces:** Sweep und WS-Discovery laufen pro aktivem IPv4-Interface.

## Logging (NLog)

- Konfiguration: `nlog.config` (wird neben die EXE kopiert, `autoReload`).
- Zielordner: `%AppData%/NetScanner/logs` (Windows) bzw. `~/.config/NetScanner/logs` (Linux).
- `netscanner-<datum>.log` — **alle Schritte** (Debug+).
- `userinput-<datum>.log` — **nur Benutzereingaben** (Logger-Name `UserInput`): Scan-Start
  mit Parametern, Abbruch, Stream-Oeffnen, Feldaenderungen.
- **Passwoerter werden nie geloggt**; RTSP-Credentials in URLs werden maskiert.

## Bewusste Grenzen

- TCP-Connect-Scan statt SYN-Scan (kein Raw-Socket → keine erhoehten Rechte).
- **Kein Passwort-Raten.** ONVIF-`GetStreamUri`/RTSP-Credentials gibst du selbst an —
  gedacht fuer Geraete im eigenen Netz.
