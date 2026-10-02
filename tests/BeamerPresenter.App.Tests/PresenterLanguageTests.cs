using System.Globalization;
using BeamerPresenter.App;

namespace BeamerPresenter.App.Tests;

public sealed class PresenterLanguageTests
{
    [Theory]
    [InlineData("de-DE", "In Firewall freigeben", "Öffentlich")]
    [InlineData("en-US", "Allow through firewall", "Public")]
    [InlineData("es-ES", "Permitir en el firewall", "Público")]
    public void Firewall_controls_and_profiles_use_the_selected_desktop_language(string culture, string button, string profile)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(button, AppText.Get("In Firewall freigeben"));
            Assert.Equal(profile, AppText.Get("Public"));
        }
        finally { CultureInfo.CurrentUICulture = previous; }
    }

    [Theory]
    [InlineData("de-DE", "de-DE")]
    [InlineData("de-AT", "de-AT")]
    [InlineData("en-GB", "en-GB")]
    [InlineData("es-MX", "es-MX")]
    [InlineData("fr-FR", "en-US")]
    public void System_language_uses_supported_family_or_english_fallback(string system, string expected) =>
        Assert.Equal(expected, PresenterLanguage.Resolve(null, CultureInfo.GetCultureInfo(system)).Name);

    [Theory]
    [InlineData("de", "de-DE")]
    [InlineData("en", "en-US")]
    [InlineData("es", "es-ES")]
    public void Saved_language_overrides_system_language(string preference, string expected) =>
        Assert.Equal(expected, PresenterLanguage.Resolve(preference, CultureInfo.GetCultureInfo("fr-FR")).Name);

    [Theory]
    [InlineData("de-DE", "Einstellungen speichern", "Gestoppt")]
    [InlineData("en-US", "Save settings", "Stopped")]
    [InlineData("es-ES", "Guardar configuración", "Detenido")]
    public void Desktop_resource_matches_selected_language(string culture, string expected, string status)
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            Assert.Equal(expected, AppText.Get("Einstellungen speichern"));
            Assert.Equal(status, AppText.Get("Stopped"));
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }
}
