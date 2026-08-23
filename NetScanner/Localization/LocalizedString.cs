using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace NetScanner.Localization;

/// <summary>
/// Bindbarer Wrapper um einen einzelnen Localization-Key. Wird von
/// <see cref="TrExtension"/> über <see cref="Get"/> beschafft (nicht
/// pro Binding neu erzeugt!) und im XAML per Binding an <see cref="Value"/>
/// konsumiert.
///
/// **Warum dieser Umweg?** Ein direktes Binding gegen den Indexer
/// <c>LocalizationService.Instance[Key]</c> braucht eine
/// <c>PropertyChanged("Item[]")</c>-Notification (WPF-Konvention) — die
/// wird von Avalonia 12 nur unzuverlässig verarbeitet: Bindings in
/// Fenstern ohne Fokus bleiben stale. Der Wrapper umgeht das mit einer
/// regulaeren <see cref="Value"/>-Property.
///
/// **Warum statisch gecacht (nicht pro Binding)?** In der ersten Version
/// dieses Wrappers wurde pro Binding ein neuer Wrapper erzeugt und in
/// einer <c>WeakReference</c>-Registry gehalten. Symptom (real passiert
/// in RenPack v0.5.1): der Live-Wechsel funktionierte weiterhin nur in
/// dem Fenster, das den Wechsel getriggert hat — die anderen Fenster
/// blieben stale. Ursache: Avalonias <c>Binding.Source</c> hält die
/// Referenz nicht dauerhaft stark; kurz nach dem ersten Rendering
/// wurden die Wrapper vom GC eingesammelt, und die Notification lief
/// ins Leere.
///
/// Der statische Cache hält für jeden Key genau einen Wrapper stark
/// für die App-Lebensdauer (typischerweise ~150 Instanzen, wenige KB
/// insgesamt). Damit ist garantiert, dass <see cref="NotifyAllChanged"/>
/// jeden aktiven Wrapper erreicht.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    public string Key { get; }
    public string Value => LocalizationService.Instance[Key];

    private static readonly Dictionary<string, LocalizedString> _cache = new(StringComparer.Ordinal);
    private static readonly object _lock = new();

    private LocalizedString(string key) => Key = key;

    /// <summary>Liefert den (gecachten) Wrapper für einen Key. Erzeugt
    /// beim ersten Zugriff, wiederverwendet danach — damit alle Bindings
    /// gegen denselben Key dieselbe Source teilen und garantiert am
    /// Leben bleiben.</summary>
    public static LocalizedString Get(string key)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(key, out var s))
            {
                s = new LocalizedString(key);
                _cache[key] = s;
            }
            return s;
        }
    }

    /// <summary>Feuert <c>PropertyChanged(nameof(Value))</c> auf jedem
    /// gecachten Wrapper. Wird vom <see cref="LocalizationService"/> beim
    /// Sprachwechsel aufgerufen.</summary>
    internal static void NotifyAllChanged()
    {
        LocalizedString[] snapshot;
        lock (_lock) snapshot = _cache.Values.ToArray();
        foreach (var s in snapshot) s.OnPropertyChanged(nameof(Value));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
