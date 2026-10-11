using System.Security.Claims;
using System.Text.Encodings.Web;
using BeamerPresenter.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BeamerPresenter.Web;

internal sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, IApiKeyService keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string PolicyName = "ManagementApi";
    public const string DocumentationPolicy = "ApiDocumentation";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-API-Key", out var header)) return AuthenticateResult.NoResult();
        if (header.Count != 1 || !await keys.VerifyAsync(header[0] ?? "", Context.RequestAborted))
            return AuthenticateResult.Fail("Invalid API credentials.");
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "API client")], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) =>
        Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Ein gültiger X-API-Key ist erforderlich.").ExecuteAsync(Context);
}
