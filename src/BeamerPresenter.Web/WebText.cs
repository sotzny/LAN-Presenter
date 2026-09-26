using System.Globalization;
using System.Text.Json;

namespace BeamerPresenter.Web;

public static class WebText
{
    private static readonly IReadOnlyDictionary<string, string[]> Resources = LoadResources();
    private static readonly string[] JavaScriptKeys =
    [
        " Lokale Wiedergabe wurde als Nächstes eingereiht.", " Lokale Wiedergabe wurde gestartet.",
        " {0} ist vorgemerkt.", "Active", "Als Nächstes", "Autoplay wurde abgelehnt.",
        "Bereit", "Bereit für die nächste Wiedergabe.", "Browser: Status nicht erreichbar",
        "Browser: {0}", "Das Video konnte nicht geladen werden.",
        "Das Video konnte nicht wiedergegeben werden.", "Dauer: lokal analysiert",
        "Dauer: {0}", "Dauer: –", "Der Download konnte nicht gestartet werden.",
        "Die Wiedergabe konnte nicht vorgemerkt werden.", "Download fehlgeschlagen.",
        "Download wird vorbereitet …", "Downloadstatus konnte nicht geladen werden.",
        "Echtzeitverbindung wird hergestellt …", "Fehler", "Getrennt", "Hidden",
        "Lokaler Download wird vorbereitet …", "Läuft", "Nicht verfügbar", "Noch nicht geprüft",
        "OK", "Paused", "Sofort", "Stopped", "Verbindung getrennt. Neuer Versuch …",
        "Verbindung nicht verfügbar. Neuer Versuch …", "Verbunden",
        "Verbunden. Bereit für die nächste Wiedergabe.", "Video ist in der Mediathek bereit.",
        "Video wird analysiert …", "Video wird lokal geladen …", "YouTube Player API timeout",
        "YouTube Player API unavailable", "YouTube Player Fehler {0}",
        "YouTube Player wird geladen …", "YouTube ist nicht verfügbar.",
        "YouTube-Link nicht erkannt.", "YouTube-Link wird geprüft …", "yt-dlp wird eingerichtet …"
    ];

    public static string Get(string key) => Resources.TryGetValue(key, out var translations)
        ? translations[CurrentLanguageIndex]
        : key;

    public static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    public static string ForError(string? detail, string fallback) =>
        ErrorKey(detail) is { } key ? Get(key) : Get(fallback);

    public static string? ErrorKey(string? detail)
    {
        var firstLine = detail?.Split('\n', 2)[0].TrimEnd('\r');
        var parameterIndex = firstLine?.IndexOf(" (Parameter '", StringComparison.Ordinal) ?? -1;
        if (parameterIndex >= 0)
        {
            firstLine = firstLine![..parameterIndex];
        }
        return firstLine is not null && Resources.ContainsKey(firstLine) ? firstLine : null;
    }

    public static string ToJson() => JsonSerializer.Serialize(JavaScriptKeys.ToDictionary(
        key => key, Get, StringComparer.Ordinal));

    private static int CurrentLanguageIndex => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "de" => 0,
        "es" => 2,
        _ => 1
    };

    private static IReadOnlyDictionary<string, string[]> LoadResources()
    {
        using var stream = typeof(WebText).Assembly.GetManifestResourceStream("BeamerPresenter.Web.UiStrings.tsv")
            ?? throw new InvalidOperationException("Localization resources are unavailable.");
        using var reader = new StreamReader(stream);
        var values = new Dictionary<string, string[]>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            var columns = line.Split('|');
            if (columns.Length != 4 || !values.TryAdd(columns[0], columns[1..]))
            {
                throw new InvalidOperationException("Localization resources contain an invalid entry.");
            }
        }
        return values;
    }
}
