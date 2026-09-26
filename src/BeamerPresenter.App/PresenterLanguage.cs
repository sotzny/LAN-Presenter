using System.Globalization;

namespace BeamerPresenter.App;

internal static class PresenterLanguage
{
    internal static CultureInfo Resolve(string? preference, CultureInfo systemCulture)
    {
        var language = preference is "de" or "en" or "es"
            ? preference
            : systemCulture.TwoLetterISOLanguageName;

        return language switch
        {
            "de" when preference is null => systemCulture,
            "en" when preference is null => systemCulture,
            "es" when preference is null => systemCulture,
            "de" => CultureInfo.GetCultureInfo("de-DE"),
            "es" => CultureInfo.GetCultureInfo("es-ES"),
            _ => CultureInfo.GetCultureInfo("en-US")
        };
    }

    internal static CultureInfo Apply(string? preference, CultureInfo systemCulture)
    {
        var culture = Resolve(preference, systemCulture);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        return culture;
    }
}
