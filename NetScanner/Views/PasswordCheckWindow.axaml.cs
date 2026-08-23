using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NetScanner.Services;

namespace NetScanner.Views;

public partial class PasswordCheckWindow : Window
{
    private readonly PwnedPasswordChecker _checker;
    private CancellationTokenSource? _cts;

    private enum ResultKind { Leaked, Safe, Neutral }

    // Parameterloser Konstruktor nur fuer den XAML-Designer.
    public PasswordCheckWindow() : this(
        new PwnedPasswordChecker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PwnedPasswordChecker>.Instance))
    { }

    public PasswordCheckWindow(PwnedPasswordChecker checker)
    {
        InitializeComponent();
        _checker = checker;
        UpdateChrome(WindowState);
        Opened += (_, _) => { WindowSizing.FitToScreen(this); PwBox.Focus(); };
    }

    // --- Custom-Chrome (analog AboutWindow) ---
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
            UpdateChrome(WindowState);
    }

    private void UpdateChrome(WindowState state)
    {
        Padding = state == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        if (this.FindControl<Control>("MaxGlyph") is { } max)
            max.IsVisible = state != WindowState.Maximized;
        if (this.FindControl<Control>("RestoreGlyph") is { } restore)
            restore.IsVisible = state == WindowState.Maximized;
    }

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object? sender, RoutedEventArgs e) => Close();
    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // --- Eingabe-Helfer ---
    private void OnRevealChanged(object? sender, RoutedEventArgs e) =>
        PwBox.RevealPassword = RevealToggle.IsChecked ?? false;

    private void OnPwKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnCheckClick(sender, e);
    }

    // --- Pruefung ---
    private async void OnCheckClick(object? sender, RoutedEventArgs e)
    {
        var pw = PwBox.Text;
        if (string.IsNullOrEmpty(pw))
        {
            StrengthBox.IsVisible = false;
            ShowResult(ResultKind.Neutral, "Bitte ein Passwort eingeben.");
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CheckBtn.IsEnabled = false;
        CheckBtn.Content = "Prüfe …";
        ResultBox.IsVisible = false;

        // Stärke sofort lokal anzeigen (offline, noch ohne Leak-Info).
        ShowStrength(PasswordStrength.Evaluate(pw, foundInLeaks: false));
        GrowForResults();

        try
        {
            var result = await _checker.CheckAsync(pw, _cts.Token);
            if (result is null)
            {
                ShowResult(ResultKind.Neutral, "Prüfung nicht möglich",
                    "Die Pwned-Passwords-API ist gerade nicht erreichbar (Internetverbindung?).");
            }
            else if (result.Found)
            {
                // Leak übersteuert die Stärke — egal wie komplex, es steht in den Listen.
                ShowStrength(PasswordStrength.Evaluate(pw, foundInLeaks: true));
                ShowResult(ResultKind.Leaked, "⚠  In Daten-Leaks gefunden",
                    $"Dieses Passwort taucht {result.Count:N0}-mal in bekannten Leaks auf. " +
                    "Ändere es auf dem betroffenen Gerät — solche Passwörter stehen in den Listen, " +
                    "die bei automatisierten Angriffen zuerst durchprobiert werden.");
            }
            else
            {
                ShowResult(ResultKind.Safe, "✓  Nicht in Leaks gefunden",
                    "Dieses Passwort taucht in der Pwned-Passwords-Datenbank nicht auf. Das ist ein gutes " +
                    "Zeichen — die Stärke-Schätzung oben sagt dir zusätzlich, wie es gegen reines Durchprobieren steht.");
            }
        }
        catch (OperationCanceledException) { /* neuer Check gestartet */ }
        finally
        {
            CheckBtn.IsEnabled = true;
            CheckBtn.Content = "Prüfen";
        }
    }

    private void ShowStrength(PasswordStrength.Result r)
    {
        Color[] palette =
        [
            Color.Parse("#E74C3C"), // 0 rot
            Color.Parse("#F0883E"), // 1 orange
            Color.Parse("#FFDD00"), // 2 gelb
            Color.Parse("#4ADE80"), // 3 hellgrün
            Color.Parse("#3FB6A8"), // 4 teal
        ];
        int score = Math.Clamp(r.Score, 0, 4);
        var on = new SolidColorBrush(palette[score]);
        var off = new SolidColorBrush(Color.Parse("#2A3742"));
        Border[] segs = [Seg0, Seg1, Seg2, Seg3, Seg4];
        for (int i = 0; i < segs.Length; i++)
            segs[i].Background = i <= score ? on : off;

        StrengthLabel.Text = r.Label;
        StrengthLabel.Foreground = on;
        CrackFast.Text = $"Gegen schnellen Hash (MD5 & Co.): {r.CrackTimeFast}";
        CrackSlow.Text = $"Gegen langsamen Hash (bcrypt & Co.): {r.CrackTimeSlow}";
        StrengthBox.IsVisible = true;
    }

    private void ShowResult(ResultKind kind, string title, string? detail = null)
    {
        (Color bg, Color border, Color text) = kind switch
        {
            ResultKind.Leaked => (Color.Parse("#3A1414"), Color.Parse("#C0392B"), Color.Parse("#E74C3C")),
            ResultKind.Safe   => (Color.Parse("#14271A"), Color.Parse("#2E7D46"), Color.Parse("#4ADE80")),
            _                 => (Color.Parse("#1E2A35"), Color.Parse("#2A3742"), Color.Parse("#8593A0")),
        };

        ResultBox.Background = new SolidColorBrush(bg);
        ResultBox.BorderBrush = new SolidColorBrush(border);
        ResultTitle.Foreground = new SolidColorBrush(text);
        ResultTitle.Text = title;
        ResultDetail.Text = detail ?? "";
        ResultDetail.IsVisible = !string.IsNullOrEmpty(detail);
        ResultBox.IsVisible = true;
    }

    /// <summary>Sobald Stärke + Ergebnis sichtbar werden, das Fenster auf die volle
    /// Inhaltshöhe bringen — FitToScreen begrenzt auf den Bildschirm, dann greift der ScrollViewer.</summary>
    private void GrowForResults()
    {
        const double full = 780;
        if (Height < full)
        {
            Height = full;
            WindowSizing.FitToScreen(this);
        }
    }

    private async void OnHibpClick(object? sender, RoutedEventArgs e)
    {
        var top = GetTopLevel(this);
        if (top is not null)
            await top.Launcher.LaunchUriAsync(new Uri("https://haveibeenpwned.com/Passwords"));
    }
}
