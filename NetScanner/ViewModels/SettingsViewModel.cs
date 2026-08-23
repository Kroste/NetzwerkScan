using CommunityToolkit.Mvvm.ComponentModel;
using NetScanner.Config;
using NetScanner.Localization;

namespace NetScanner.ViewModels;

/// <summary>Eine wählbare UI-Sprache: nativer Name mit Länderflagge.</summary>
public sealed record UiCultureOption(string Iso, string Display)
{
    public override string ToString() => Display;
}

public sealed partial class SettingsViewModel : ViewModelBase
{
    private readonly AppSettingsService _settings;

    public SettingsViewModel(AppSettingsService settings)
    {
        _settings = settings;

        SupportedUiCultures = LocalizationService.SupportedCultures
            .Select(c => new UiCultureOption(c.Iso, $"{c.Flag}  {c.Display}"))
            .ToList();

        string active = LocalizationService.Instance.CurrentIso;
        _selectedUiCulture = SupportedUiCultures.FirstOrDefault(c => c.Iso == active)
                             ?? SupportedUiCultures[0];
    }

    public IReadOnlyList<UiCultureOption> SupportedUiCultures { get; }

    [ObservableProperty] private UiCultureOption _selectedUiCulture;

    /// <summary>
    /// Sprachwechsel wirkt sofort in allen Fenstern und wird direkt persistiert.
    /// Es gibt kein Speichern/Abbrechen in diesem Fenster, deshalb wäre eine
    /// reine Vorschau hier nur verwirrend.
    /// </summary>
    partial void OnSelectedUiCultureChanged(UiCultureOption value)
    {
        LocalizationService.Instance.SetCulture(value.Iso);
        _settings.Update(s => s with { UiCulture = value.Iso });
    }
}
