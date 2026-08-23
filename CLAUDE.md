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

Stand nach dem vollständigen Kroste-Standard-Refresh (2026-08-23). Alle Punkte
der Definition of Done sind erfüllt; die Roadmap unten enthält nur noch echte
Feature-Ideen, keine offenen Standard-Lücken mehr.

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

**Nachgezogen im Standard-Refresh:**

- **Logging:** `Logging/MaskingLayoutRenderer` (Registrierung per `[ModuleInitializer]`)
  maskiert Passwörter, Tokens, URL-Credentials und Auth-Header in jedem Target.
  Der Credential-Audit loggt nur noch den Benutzernamen, nie das Passwort.
- **Struktur:** `NetScanner/` + `NetScanner.Tests/`, `.slnx`, Central Package
  Management, `Directory.Build.props`, `TreatWarningsAsErrors`, MinVer, LICENSE.
- **CI:** `ci.yml` (Build + Tests bei Push/PR), `dependabot.yml`, Release-Workflow
  auf Node-24-Action-Majors und um Tests erweitert.
- **Chrome:** `Views/ChromeWindow` als Basisklasse aller Fenster (`BorderOnly`,
  nicht `None`), `Controls/TitleBar` mit `LandedOnInteractiveChild`-Guard.
- **Look:** Kroste-Palette und volle Style-Bibliothek in `App.axaml`, Card-Look,
  keine Farbliterale mehr außerhalb von `App.axaml` (auch nicht im Code-Behind —
  `Views/Palette.cs` löst Keys auf).
- **System-Tray:** `Views/TrayController`, Minimieren legt ab, Schließen beendet.
- **Update:** `Services/UpdateService` mit echtem Self-Update (Windows-ZIP,
  Linux-tar.gz, AppImage) und Update-Abzeichen in der Titelleiste.
- **Localization:** EN + DE mit Live-Wechsel, Sprachauswahl im neuen
  `SettingsWindow`, Persistenz über `Config/AppSettingsService`.
- **Umlaute:** echte Umlaute statt `ae/oe/ue/ss` im gesamten Repo.
- **Tests:** 121 Tests (Masking, Update-Versionslogik, Localization-Konsistenz,
  IpRangeHelper, OuiLookup, PasswordStrength, DeviceClassifier).
- **Pakete:** Avalonia 12.1.1, CommunityToolkit.Mvvm 8.4.2, Tmds.DBus.Protocol
  explizit gepinnt; `Microsoft.Win32.Registry` entfernt.
- **Icon:** reproduzierbar aus `scripts/build_icon.py` (+ PowerShell-Port), jetzt
  in Kroste-Blau/Gold, inklusive Multi-Res-`.ico`.

## Roadmap

Offene Feature-Ideen, keine Standard-Lücken mehr:

1. Scan-Ergebnisse persistieren und zwischen Läufen vergleichen („was ist neu im
   Netz?"). Muster dafür steht in `references/persistence.md`.
2. Zeitgesteuerter Wiederholungs-Scan mit Tray-Benachrichtigung bei neuen Geräten.
3. Weitere Sprachen über EN + DE hinaus (die Infrastruktur trägt sie bereits).
4. IPv6-Discovery — heute ist alles strikt IPv4.
5. Prüfen, ob eine KI-Auswertung der Scan-Ergebnisse echten Mehrwert bringt
   (z. B. „welche Geräte fallen aus dem Rahmen?"). Falls ja: Multi-Provider nach
   Allpaca-Vorbild, Ollama als Default.

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
  `global.json`). VSTest-Flags wie `--nologo` werden an die Test-Exe durchgereicht,
  die sie nicht kennt — der Lauf bricht dann mit „Es wurden keine Tests ausgeführt"
  und Exitcode 5 ab, was wie ein kaputtes Testprojekt aussieht. Einfach weglassen;
  `dotnet test` auf der Solution funktioniert.
- **Services liefern Resource-Keys, keine fertigen Texte.** `DeviceClassifier`
  gibt `Device_*`-Keys zurück (und Klartext für Produktnamen und UPnP-Angaben),
  `PasswordStrength` zusätzlich die Form `Key|Zahl` für Zeitspannen. Übersetzt
  wird erst in der UI über `L.TOrText`. Damit bleiben die Services ohne
  Localization-Abhängigkeit testbar — und ein vergessener Key fällt im
  `DeviceClassifierTests` auf, nicht erst beim Nutzer.
- **Kein `Classes="…"` ohne Style in `App.axaml`.** Avalonia meldet eine tote
  Style-Klasse nicht, sie rendert einfach falsch. Nach jedem Style-Refactoring
  den Abgleich referenzierter gegen definierte Klassen laufen lassen.
- **Farbliterale gehören ausschließlich in `App.axaml`.** Im Code-Behind löst
  `Views/Palette.cs` die Keys auf und fällt bei einem Tippfehler sichtbar auf
  Magenta zurück.
- **Kein doppelter Bindestrich in XML-Kommentaren** (`Directory.Packages.props`,
  csproj): das ist ein Parser-Fehler und macht den kompletten umgebenden Block
  unsichtbar — Symptom war NU1010 für jedes einzelne Paket.
