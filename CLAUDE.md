# NetScanner

## Grundlagen

- **Was:** Desktop-Werkzeug zur Analyse des eigenen LAN — Host-Discovery, Portscan,
  Geräte-Fingerprinting, ONVIF/RTSP-Kameraerkennung mit Live-Vorschau, interaktive
  Netzwerkkarte, Exposure-Check (UPnP-Portfreigaben) und Passwort-Prüfung (HIBP).
- **Repository:** https://github.com/Kroste/NetzwerkScan — **Achtung:** der Repo-Slug
  heißt `NetzwerkScan`, nicht `NetScanner`. Update-URLs und Badges müssen auf den Slug
  zeigen, sonst laufen sie ins 404.
- **Stack:** C# / .NET 10 / Avalonia 12.1, CommunityToolkit.Mvvm,
  Microsoft.Extensions.DependencyInjection, NLog (mit Secret-Masking),
  xunit.v3 + FluentAssertions 7.x, LibVLCSharp (Core, ohne gebündeltes libvlc).
- **Struktur:** Flach (kein `src/`): `NetScanner/` + `NetScanner.Tests/`, `.slnx`,
  Central Package Management (`Directory.Packages.props`), `Directory.Build.props`,
  MinVer (Tags `v*`).
- **Versionierung:** MinVer — die Version kommt **ausschließlich** aus dem Git-Tag.
  Es gibt keine `<Version>` in der csproj mehr; Release über `scripts/release.sh`
  bzw. den VS-Code-Task „release (tag + push)".
- **Konventionen:** GlobalExceptionHandler, AboutWindow mit Version + BMC-Button,
  `TreatWarningsAsErrors`, alle Fenster erben von `ChromeWindow`.
- **Kommunikation:** Deutsch, „du". Lars entwirft, Claude implementiert.

## Aktueller Stand

Stand v1.5.x, aktuell in Nacharbeit auf den Kroste-Standard.

**Funktioniert:**

- Scan-Pipeline über `ScanOrchestrator`: Ping-Sweep (`NetworkScanner`), ARP-Auflösung,
  Portscan (`PortScanner`), Banner-Grab, mDNS/SSDP/NetBIOS-Discovery,
  OUI-Herstellerauflösung, Geräteklassifikation (`DeviceClassifier`).
- ONVIF-Kameraerkennung (`OnvifDiscovery`) + RTSP-Probe (`RtspProbe`) mit
  Live-Vorschau über eine vorhandene VLC-Installation (`VlcLocator` — libvlc wird
  **nicht** gebündelt, das spart ~85 MB pro Windows-Build).
- Optionaler Credential-Audit (`CredentialAuditor`): kuratierte, öffentlich
  dokumentierte Werks-Logins, **kein Brute-Force**, opt-in. Das gefundene Passwort
  wird **nie** geloggt, nur der Benutzername.
- Exposure-Check (`UpnpExposureProbe`), Traceroute, Wake-on-LAN,
  Passwortstärke + HIBP-k-Anonymity-Abfrage (`PwnedPasswordChecker`).
- Fenster: MainWindow, NetworkMapWindow, ExposureWindow, PasswordCheckWindow,
  AboutWindow.

**Zuletzt nachgezogen (Standard-Refresh):**

- Secret-Masking im Log (`Logging/MaskingLayoutRenderer`, Registrierung per
  `[ModuleInitializer]`), Klartext-Passwort aus dem Credential-Audit-Log entfernt.
- Repo-Struktur auf den Kanon: `NetScanner/` + `NetScanner.Tests/`, `.slnx`, CPM,
  `Directory.Build.props`, MinVer, `TreatWarningsAsErrors`, LICENSE.
- CI-Workflow (Build + Tests bei Push/PR) und Dependabot ergänzt; Release-Workflow
  auf Node-24-Action-Majors gehoben und um Tests erweitert.
- Avalonia 12.0.4 → 12.1.1 (natives Wayland-Backend, relevant auf Bazzite/KDE).
- App-Icon reproduzierbar aus `scripts/build_icon.py` (+ PowerShell-Port), jetzt
  auch als Multi-Res-`.ico` für `<ApplicationIcon>`.

## Roadmap

Offene Punkte aus dem Standard-Abgleich, in dieser Reihenfolge:

1. `ChromeWindow`-Basisklasse + `Controls/TitleBar` — aktuell setzt jedes Fenster
   sein Chrome selbst und nutzt `WindowDecorations="None"` (killt die Resize-Griffe;
   korrekt wäre `BorderOnly`).
2. Styles zentral nach `App.axaml` (heute pro Fenster dupliziert), Card-Look
   (`Border.card` / `card-flat`, `h1`/`h2`/`section-label`) einführen und die
   hardcodeten `#XXXXXX`-Farbliterale in den Views durch Palette-Keys ersetzen.
3. System-Tray (`TrayController`): Minimieren → Tray, Schließen beendet.
4. `UpdateService` mit **echtem Self-Update** gegen GitHub Releases (nicht nur
   Notification) — Referenz: RenPack `Services/UpdateService.cs`.
5. Localization EN + DE (`Localization/` mit `LocalizationService`,
   `LocalizedString`, `TrExtension`, Resx) — die UI ist heute hart deutsch.
6. Umlaute-Sweep: Kommentare, XML-Doc und Log-Texte nutzen teils noch die
   Ersatzschreibweise `ae/oe/ue/ss`.
7. Testabdeckung ausbauen — `IpRangeHelper`, `PasswordStrength`, `DeviceClassifier`,
   `OuiLookup` sind reine Logik und ohne Umbau testbar.

## Referenz

- **Kein Brute-Force, keine Wordlists.** Wurde mehrfach abgelehnt und gilt dauerhaft.
  NetScanner ist strikt auf vom Betreiber kontrollierte Netze beschränkt; der
  Credential-Audit prüft nur eine kurze, kuratierte Liste dokumentierter Werks-Logins
  und ist opt-in.
- **Secrets nie ins Log.** Der Credential-Audit findet *wirksame* Logins für reale
  Geräte. Geloggt wird nur der Benutzername; das vollständige Paar bleibt im
  Rückgabewert und damit in der UI. `${masked:inner=…}` liegt zusätzlich in jedem
  Log-Target.
- **libvlc wird nicht gebündelt.** `VlcLocator` sucht zur Laufzeit:
  `NETSCANNER_VLC_DIR` → Windows-Registry → `%ProgramFiles%\VideoLAN\VLC` bzw.
  System-libvlc unter Linux. Fehlt VLC, läuft die App weiter, nur ohne Vorschau.
  `LibVLCSharp.Avalonia` darf **nicht** referenziert werden — das Paket hängt an
  Avalonia 11.
- **`Microsoft.Win32.Registry` braucht keinen PackageReference.** Die Typen liegen
  seit .NET 5 im Framework; das Kompatibilitätspaket löst NU1510 aus und bricht mit
  `TreatWarningsAsErrors` den Build.
- **NLog `internalLogFile` kennt keine Layout-Renderer.** Ein `${specialfolder:…}`
  dort erzeugt ein literales Verzeichnis dieses Namens. Erlaubt sind nur
  `${basedir}`, `${currentdir}`, `${tempdir}` und `%ENVVAR%`.
- **Der Masking-Renderer wird per `[ModuleInitializer]` registriert**, nicht in
  `Program.Main` — der Testprozess hat kein `Main`. Im Test muss der Modul-Konstruktor
  mit `RuntimeHelpers.RunModuleConstructor` erzwungen werden; ein `typeof()` allein
  löst ihn nicht aus.
- **`dotnet test` läuft über die Microsoft.Testing.Platform** (Runner-Block in der
  `global.json`). `--nologo` und andere VSTest-Flags werden durchgereicht und
  brechen den Lauf ab.
