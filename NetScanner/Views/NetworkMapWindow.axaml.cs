using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using NetScanner.Models;
using NetScanner.Services;
using NetScanner.ViewModels;

namespace NetScanner.Views;

public partial class NetworkMapWindow : Window
{
    private MainViewModel? _vm;
    private TracerouteService? _trace;
    private CancellationTokenSource? _traceCts;
    private Border? _selectedNode;

    // --- Farben (dunkles Theme; spiegeln App.axaml + Visualizer-Palette) ---
    private static readonly IBrush SurfaceBrush    = new SolidColorBrush(Color.Parse("#172029"));
    private static readonly IBrush AccentSoftBrush = new SolidColorBrush(Color.Parse("#1E3A38"));
    private static readonly IBrush LinkBrush       = new SolidColorBrush(Color.Parse("#3A4752"));
    private static readonly IBrush TextPrimaryBrush= new SolidColorBrush(Color.Parse("#E6EDF3"));
    private static readonly IBrush TextMutedBrush  = new SolidColorBrush(Color.Parse("#8593A0"));
    private static readonly IBrush AccentBrush     = new SolidColorBrush(Color.Parse("#3FB6A8"));
    private static readonly IBrush CameraBrush     = new SolidColorBrush(Color.Parse("#F0883E"));
    private static readonly IBrush BlueBrush       = new SolidColorBrush(Color.Parse("#378ADD"));
    private static readonly IBrush GreenBrush      = new SolidColorBrush(Color.Parse("#7BB661"));
    private static readonly IBrush PurpleBrush     = new SolidColorBrush(Color.Parse("#8E86E0"));
    private static readonly IBrush AmberBrush      = new SolidColorBrush(Color.Parse("#D9A441"));
    private static readonly IBrush PinkBrush       = new SolidColorBrush(Color.Parse("#D4789E"));
    private static readonly IBrush MutedBrush      = TextMutedBrush;
    private static readonly IBrush GatewayBrush    = AccentBrush;

    // Designer-Konstruktor
    public NetworkMapWindow()
    {
        InitializeComponent();
        UpdateChrome(WindowState);
    }

    public NetworkMapWindow(MainViewModel vm) : this()
    {
        _vm = vm;
        _trace = App.Services.GetRequiredService<TracerouteService>();

        BuildLegend();
        MapCanvas.AttachedToVisualTree += (_, _) => DrawMap();
        MapCanvas.SizeChanged += (_, _) => DrawMap();
    }

    // ===== Stern-Karte =====
    private void DrawMap()
    {
        MapCanvas.Children.Clear();
        _selectedNode = null;

        var hosts = _vm?.Hosts?.ToList() ?? [];
        EmptyHint.IsVisible = hosts.Count == 0;
        if (hosts.Count == 0) return;

        double w = MapCanvas.Bounds.Width, h = MapCanvas.Bounds.Height;
        if (w < 50 || h < 50) return;
        double cx = w / 2, cy = h / 2;

        // Gateway bestimmen: echtes Default-Gateway, sonst per UPnP/Device-Typ
        var gwIp = TracerouteService.DefaultGateway();
        HostResult? gateway = gwIp is not null ? hosts.FirstOrDefault(x => x.Address.Equals(gwIp)) : null;
        gateway ??= hosts.FirstOrDefault(x => Contains(x.UpnpDeviceType, "Router") || Contains(x.DeviceType, "Router"));

        var others = hosts.Where(x => !ReferenceEquals(x, gateway)).ToList();
        int n = others.Count;
        double radius = Math.Max(70, Math.Min(w, h) / 2 - 100);

        var pts = new List<(HostResult host, double x, double y)>(n);
        for (int i = 0; i < n; i++)
        {
            double angle = -Math.PI / 2 + i * (2 * Math.PI / Math.Max(1, n));
            pts.Add((others[i], cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle)));
        }

        // 1) Verbindungslinien zuerst (liegen hinter den Knoten)
        foreach (var p in pts)
            MapCanvas.Children.Add(new Line
            {
                StartPoint = new Point(cx, cy),
                EndPoint = new Point(p.x, p.y),
                Stroke = LinkBrush,
                StrokeThickness = 1.2,
                Opacity = 0.55
            });

        // 2) Geräte-Knoten
        foreach (var p in pts)
            AddNode(p.host, p.x, p.y, isGateway: false);

        // 3) Gateway zuletzt -> liegt oben
        if (gateway is not null)
            AddNode(gateway, cx, cy, isGateway: true);
        else
            AddCenterNote(cx, cy);
    }

    private void AddNode(HostResult host, double cx, double cy, bool isGateway)
    {
        double width = isGateway ? 156 : 136;
        var borderColor = isGateway ? GatewayBrush : BrushFor(host);

        bool hasName = host.HasBestName;
        string title = hasName ? host.BestName! : host.Address.ToString();
        string sub = isGateway
            ? "Gateway"
            : hasName ? host.Address.ToString()
            : (!string.IsNullOrWhiteSpace(host.DeviceType) ? host.DeviceType!
               : host.IsCamera ? "Kamera"
               : host.OsGuess ?? "Host");

        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = title, FontSize = 12.5, FontWeight = FontWeight.Bold,
            Foreground = TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = sub, FontSize = 10.5, Foreground = TextMutedBrush,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var border = new Border
        {
            Background = SurfaceBrush,
            BorderBrush = borderColor,
            BorderThickness = new Thickness(isGateway ? 2.2 : 1.6),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11, 7),
            Width = width,
            Child = panel,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = host
        };
        border.PointerPressed += OnNodePressed;
        ToolTip.SetTip(border, BuildTip(host));

        Canvas.SetLeft(border, cx - width / 2);
        Canvas.SetTop(border, cy - 22);
        MapCanvas.Children.Add(border);
    }

    private void AddCenterNote(double cx, double cy)
    {
        var border = new Border
        {
            Background = SurfaceBrush, BorderBrush = LinkBrush, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 7), Width = 150,
            Child = new TextBlock { Text = "Gateway nicht im Scan", FontSize = 11,
                                    Foreground = TextMutedBrush, TextWrapping = TextWrapping.Wrap }
        };
        Canvas.SetLeft(border, cx - 75);
        Canvas.SetTop(border, cy - 22);
        MapCanvas.Children.Add(border);
    }

    private void OnNodePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Border b || b.Tag is not HostResult host) return;
        if (_vm is not null) _vm.SelectedHost = host;

        if (_selectedNode is not null) _selectedNode.Background = SurfaceBrush;
        b.Background = AccentSoftBrush;
        _selectedNode = b;
    }

    private static IBrush BrushFor(HostResult h)
    {
        if (h.IsCamera) return CameraBrush;
        string t = $"{h.DeviceType} {h.OsGuess} {h.UpnpDeviceType}".ToLowerInvariant();
        if (t.Contains("router") || t.Contains("gateway")) return GatewayBrush;
        if (t.Contains("drucker") || t.Contains("print")) return AmberBrush;
        if (t.Contains("nas") || t.Contains("storage") || t.Contains("synology") || t.Contains("speicher")) return PurpleBrush;
        if (t.Contains("kamera") || t.Contains("camera")) return CameraBrush;
        if (t.Contains("windows")) return BlueBrush;
        if (t.Contains("linux") || t.Contains("unix") || t.Contains("android")) return GreenBrush;
        if (t.Contains("media") || t.Contains("tv")) return PinkBrush;
        if (t.Contains("mobil") || t.Contains("phone") || t.Contains("handy")) return MutedBrush;
        return MutedBrush;
    }

    private static bool Contains(string? s, string sub) =>
        s is not null && s.Contains(sub, StringComparison.OrdinalIgnoreCase);

    private static string BuildTip(HostResult h)
    {
        var sb = new StringBuilder();
        sb.Append(h.Address);
        if (h.HasBestName) sb.Append("  ·  ").Append(h.BestName);
        if (h.HasDeviceInfo) sb.Append('\n').Append(h.DeviceSummary);
        if (h.HasMac) sb.Append('\n').Append(h.MacAddress);
        if (h.OpenPorts.Count > 0) sb.Append('\n').Append(h.OpenPortsDisplay);
        return sb.ToString();
    }

    private void BuildLegend()
    {
        AddLegend(GatewayBrush, "Gateway / Router");
        AddLegend(BlueBrush, "Windows");
        AddLegend(GreenBrush, "Linux / Android");
        AddLegend(CameraBrush, "Kamera");
        AddLegend(PurpleBrush, "NAS / Speicher");
        AddLegend(AmberBrush, "Drucker");
        AddLegend(MutedBrush, "Mobil / sonstige");
    }

    private void AddLegend(IBrush c, string label)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new Border
        {
            Width = 11, Height = 11, CornerRadius = new CornerRadius(3),
            Background = SurfaceBrush, BorderBrush = c, BorderThickness = new Thickness(1.6),
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = label, FontSize = 11, Foreground = TextMutedBrush,
            VerticalAlignment = VerticalAlignment.Center
        });
        LegendPanel.Children.Add(row);
    }

    // ===== Traceroute (Außenpfad) =====
    private async void OnTrace(object? sender, RoutedEventArgs e)
    {
        if (_trace is null) return;

        string? target = TraceTarget.Text?.Trim();
        if (string.IsNullOrWhiteSpace(target))
            target = TracerouteService.DefaultGateway()?.ToString();
        if (string.IsNullOrWhiteSpace(target))
        {
            TraceStatus.Text = "Kein Ziel angegeben und kein Gateway gefunden.";
            return;
        }

        HopPanel.Children.Clear();
        AddStartPill();
        TraceStatus.Text = $"Verfolge Weg zu {target} …";
        TraceBtn.IsEnabled = false;
        TraceCancelBtn.IsVisible = true;
        _traceCts = new CancellationTokenSource();

        var progress = new Progress<TraceHop>(AddHopPill);
        try
        {
            var hops = await _trace.TraceAsync(target!, progress: progress, ct: _traceCts.Token);
            bool reached = hops.Count > 0 && hops[^1].Reached;
            TraceStatus.Text = reached
                ? $"Ziel {target} erreicht in {hops.Count} Hops."
                : $"Beendet nach {hops.Count} Hops — Ziel nicht erreicht oder letzte Hops antworten nicht (häufig: Firewall am Ziel).";
        }
        catch (OperationCanceledException) { TraceStatus.Text = "Traceroute abgebrochen."; }
        catch (Exception ex) { TraceStatus.Text = "Fehler: " + ex.Message; }
        finally
        {
            TraceBtn.IsEnabled = true;
            TraceCancelBtn.IsVisible = false;
            _traceCts?.Dispose();
            _traceCts = null;
        }
    }

    private void OnTraceCancel(object? sender, RoutedEventArgs e) => _traceCts?.Cancel();

    private void AddStartPill() =>
        HopPanel.Children.Add(MakePill("Dieser PC", "Start", AccentBrush, accentFill: true));

    private void AddHopPill(TraceHop hop)
    {
        HopPanel.Children.Add(MakeArrow());
        var color = hop.Reached ? AccentBrush : hop.TimedOut ? LinkBrush : MutedBrush;
        HopPanel.Children.Add(MakePill(hop.AddressDisplay, $"#{hop.Ttl} · {hop.RttDisplay}", color, accentFill: hop.Reached));
    }

    private static Border MakePill(string title, string subtitle, IBrush color, bool accentFill)
    {
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 12.5, FontWeight = FontWeight.Bold, Foreground = TextPrimaryBrush });
        if (!string.IsNullOrEmpty(subtitle))
            panel.Children.Add(new TextBlock { Text = subtitle, FontSize = 10.5, Foreground = TextMutedBrush });
        return new Border
        {
            Background = accentFill ? AccentSoftBrush : SurfaceBrush,
            BorderBrush = color, BorderThickness = new Thickness(1.4),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(11, 6),
            VerticalAlignment = VerticalAlignment.Center, Child = panel
        };
    }

    private static TextBlock MakeArrow() => new()
    {
        Text = "→", FontSize = 15, Foreground = TextMutedBrush,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(7, 0)
    };

    private void OnRefresh(object? sender, RoutedEventArgs e) => DrawMap();

    // ===== Fenster-Chrome =====
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateChrome(WindowState);
    }

    private void UpdateChrome(WindowState state)
    {
        Padding = state == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (this.FindControl<Control>("MaxGlyph") is { } max) max.IsVisible = state != WindowState.Maximized;
        if (this.FindControl<Control>("RestoreGlyph") is { } restore) restore.IsVisible = state == WindowState.Maximized;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
