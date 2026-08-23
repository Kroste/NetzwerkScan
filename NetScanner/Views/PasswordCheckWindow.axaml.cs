using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using NetScanner.Services;
using NetScanner.Localization;

namespace NetScanner.Views;

public partial class PasswordCheckWindow : ChromeWindow
{
    private readonly PwnedPasswordChecker _checker;
    private CancellationTokenSource? _cts;

    private enum ResultKind { Leaked, Safe, Neutral }

    // Parameterloser Konstruktor nur für den XAML-Designer.
    public PasswordCheckWindow() : this(
        new PwnedPasswordChecker(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PwnedPasswordChecker>.Instance))
    { }

    public PasswordCheckWindow(PwnedPasswordChecker checker)
    {
        InitializeComponent();
        _checker = checker;
        Opened += (_, _) => { WindowSizing.FitToScreen(this); PwBox.Focus(); };
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // --- Eingabe-Helfer ---
    private void OnRevealChanged(object? sender, RoutedEventArgs e) =>
        PwBox.RevealPassword = RevealToggle.IsChecked ?? false;

    private void OnPwKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnCheckClick(sender, e);
    }

    // --- Prüfung ---
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
        CheckBtn.Content = L.T("Pw_Checking");
        ResultBox.IsVisible = false;

        // Stärke sofort lokal anzeigen (offline, noch ohne Leak-Info).
        ShowStrength(PasswordStrength.Evaluate(pw, foundInLeaks: false));
        GrowForResults();

        try
        {
            var result = await _checker.CheckAsync(pw, _cts.Token);
            if (result is null)
            {
                ShowResult(ResultKind.Neutral, L.T("Pw_NotPossible"), L.T("Pw_NotPossible_Detail"));
            }
            else if (result.Found)
            {
                // Leak übersteuert die Stärke — egal wie komplex, es steht in den Listen.
                ShowStrength(PasswordStrength.Evaluate(pw, foundInLeaks: true));
                ShowResult(ResultKind.Leaked, L.T("Pw_Leaked"),
                    L.F("Pw_Leaked_Detail", result.Count.ToString("N0")));
            }
            else
            {
                ShowResult(ResultKind.Safe, L.T("Pw_Safe"), L.T("Pw_Safe_Detail"));
            }
        }
        catch (OperationCanceledException) { /* neuer Check gestartet */ }
        finally
        {
            CheckBtn.IsEnabled = true;
            CheckBtn.Content = L.T("Common_Check");
        }
    }

    private void ShowStrength(PasswordStrength.Result r)
    {
        int score = Math.Clamp(r.Score, 0, 4);
        var on = Palette.Brush($"NetStrength{score}Brush");
        var off = Palette.Brush("KrosteBorderBrush");
        Border[] segs = [Seg0, Seg1, Seg2, Seg3, Seg4];
        for (int i = 0; i < segs.Length; i++)
            segs[i].Background = i <= score ? on : off;

        // PasswordStrength liefert Resource-Keys (teils mit "Key|Zahl"), damit die
        // Klasse ohne Localization-Abhängigkeit testbar bleibt.
        StrengthLabel.Text = L.TOrText(r.Label);
        StrengthLabel.Foreground = on;
        CrackFast.Text = L.F("Pw_CrackFast", L.TOrText(r.CrackTimeFast));
        CrackSlow.Text = L.F("Pw_CrackSlow", L.TOrText(r.CrackTimeSlow));
        StrengthBox.IsVisible = true;
    }

    private void ShowResult(ResultKind kind, string title, string? detail = null)
    {
        (string bg, string border, string text) = kind switch
        {
            ResultKind.Leaked => ("NetVulnSoftBrush", "NetVulnBorderBrush", "NetVulnBrush"),
            ResultKind.Safe   => ("NetSuccessSoftBrush", "NetSuccessBorderBrush", "NetSuccessTextBrush"),
            _                 => ("KrosteSurfaceBrush", "KrosteBorderBrush", "KrosteMutedTextBrush"),
        };

        ResultBox.Background = Palette.Brush(bg);
        ResultBox.BorderBrush = Palette.Brush(border);
        ResultTitle.Foreground = Palette.Brush(text);
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
