using System.Net;
using System.Security.Cryptography;
using BeamerPresenter.Application;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BeamerPresenter.Web;

internal static class DeveloperActions
{
    public static void MapDeveloperActions(this IEndpointRouteBuilder app) =>
        app.MapPost("/developer/key/{action}", ChangeKeyAsync).RequireAuthorization().ExcludeFromDescription();

    private static async Task<IResult> ChangeKeyAsync(string action, HttpContext context, IApiKeyService keys,
        IAntiforgery antiforgery, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(); }
        switch (action)
        {
            case "disable":
                await keys.DisableAsync(ct);
                return Results.Redirect("/developer?key=disabled");
            case "set":
                var form = await context.Request.ReadFormAsync(ct);
                try { await keys.SetKeyAsync(form["key"].ToString(), ct); }
                catch (ArgumentException) { return Results.Redirect("/developer?key=invalid"); }
                return Results.Redirect("/developer?key=set");
            case "generate":
                var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
                await keys.SetKeyAsync(key, ct);
                var language = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return Results.Content($"""
                    <!doctype html><html lang="{language}"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
                    <title>{WebUtility.HtmlEncode(WebText.Get("API-Key erzeugt"))}</title>
                    <link rel="stylesheet" href="/_content/BeamerPresenter.Web/css/app.css"></head>
                    <body><main class="generated-key-page"><h1>{WebUtility.HtmlEncode(WebText.Get("API-Key erzeugt"))}</h1>
                    <p>{WebUtility.HtmlEncode(WebText.Get("Diesen Key jetzt kopieren. Er wird nur einmal angezeigt. Der vorherige Key ist ungültig."))}</p>
                    <label for="generated-api-key">X-API-Key</label><input id="generated-api-key" value="{key}" readonly autocomplete="off">
                    <p><a href="/developer">{WebUtility.HtmlEncode(WebText.Get("Zurück zu Developer"))}</a></p></main></body></html>
                    """, "text/html; charset=utf-8");
            default: return Results.NotFound();
        }
    }
}
