using System.Globalization;
using System.Resources;

namespace BeamerPresenter.App;

internal static class AppText
{
    private static readonly ResourceManager Resources = new("BeamerPresenter.App.UiStrings", typeof(AppText).Assembly);

    internal static string Get(string key) =>
        Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    internal static string Format(string key, params object?[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);
}
