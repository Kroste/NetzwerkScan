using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using NetScanner.Localization;
using NetScanner.Services;

namespace NetScanner.Views;

/// <summary>
/// Zeigt die Seiten eines Geräte-Webinterfaces, die der Server selbst preisgibt
/// (Links, robots.txt, sitemap.xml). Kein Pfad-Raten — siehe <see cref="WebPageScanner"/>.
/// </summary>
public partial class WebPagesWindow : ChromeWindow
{
    private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

    private readonly WebPageScanner _scanner;
    private readonly string _baseUrl;
    private CancellationTokenSource? _cts;

    /// <summary>Streamende Ergebnisliste (an das ItemsControl gebunden).</summary>
    public ObservableCollection<WebPage> Pages { get; } = [];

    // Parameterloser Konstruktor nur für den XAML-Designer.
    public WebPagesWindow() : this(new WebPageScanner(NullLogger<WebPageScanner>.Instance), "http://192.168.10.1")
    { }

    public WebPagesWindow(WebPageScanner scanner, string baseUrl)
    {
        InitializeComponent();
        _scanner = scanner;
        _baseUrl = baseUrl;

        BaseUrlText.Text = baseUrl;
        PageList.ItemsSource = Pages;

        Opened += async (_, _) => { WindowSizing.FitToScreen(this); await CrawlAsync(); };
        Closed += (_, _) => _cts?.Cancel();
    }

    private async Task CrawlAsync()
    {
        Busy.IsVisible = true;
        StatusText.Text = L.T("WebPages_Running");
        _cts = new CancellationTokenSource();

        try
        {
            await foreach (var page in _scanner.CrawlAsync(_baseUrl, _cts.Token))
            {
                Pages.Add(page);
                StatusText.Text = L.F("WebPages_Count", Pages.Count);
            }

            StatusText.Text = Pages.Count == 0
                ? L.T("WebPages_None")
                : L.F("WebPages_Done", Pages.Count);
        }
        catch (OperationCanceledException)
        {
            // Fenster geschlossen -> nichts weiter.
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Web-Crawl fehlgeschlagen");
            StatusText.Text = L.T("WebPages_Failed");
        }
        finally
        {
            Busy.IsVisible = false;
        }
    }

    private void OnPageClick(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: WebPage page })
        {
            try
            {
                GetTopLevel(this)?.Launcher.LaunchUriAsync(new Uri(page.Url));
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Seite konnte nicht geöffnet werden: {Url}", page.Url);
            }
        }
    }
}
