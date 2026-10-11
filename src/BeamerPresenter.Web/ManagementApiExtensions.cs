using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;

namespace BeamerPresenter.Web;

internal static class ManagementApiExtensions
{
    public static IServiceCollection AddManagementApi(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        services.AddAuthorization(options =>
        {
            options.AddPolicy(ApiKeyAuthenticationHandler.PolicyName, policy => policy
                .AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
            options.AddPolicy(ApiKeyAuthenticationHandler.DocumentationPolicy, policy => policy
                .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme, ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser());
        });
        services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
        services.AddOpenApi("v1", options =>
        {
            options.ShouldInclude = description => description.GroupName == "v1";
            options.AddSchemaTransformer((schema, context, _) =>
            {
                if (context.JsonTypeInfo.Type == typeof(NewsRequest))
                    schema.Example = JsonNode.Parse("""{"title":"Turnierstart","text":"Bitte zur Turnierleitung kommen.","mode":"Ticker","durationSeconds":120,"permanent":false,"priority":0,"showNow":true}""");
                if (context.JsonTypeInfo.Type == typeof(VideoPlaybackRequest))
                    schema.Example = JsonNode.Parse("""{"mediaId":1,"startSeconds":0,"durationSeconds":120}""");
                if (context.JsonTypeInfo.Type == typeof(YouTubeRequest))
                    schema.Example = JsonNode.Parse("""{"url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","maximumDurationSeconds":120,"playbackMode":"Automatic"}""");
                return Task.CompletedTask;
            });
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new OpenApiInfo
                {
                    Title = "Beamer Presenter Verwaltungs-API",
                    Version = "v1",
                    Description = "Steuerung mit X-API-Key. Dauer und Position in Sekunden, Zeitpunkte mit Zeitzone. Software-Updates und Key-Verwaltung sind nicht Teil dieser API."
                };
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["ApiKey"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-API-Key" }
                };
                document.Security = [new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("ApiKey", document)] = [] }];
                return Task.CompletedTask;
            });
        });
        return services;
    }

    public static void MapManagementApi(this IEndpointRouteBuilder app)
    {
        app.MapOpenApi("/openapi/{documentName}.json").RequireAuthorization(ApiKeyAuthenticationHandler.DocumentationPolicy).ExcludeFromDescription();
        var middleware = ((IApplicationBuilder)app).New();
        middleware.UseSwaggerUI(options =>
        {
            options.RoutePrefix = "swagger";
            options.SwaggerEndpoint("/openapi/v1.json", "Verwaltungs-API v1");
            options.ConfigObject.PersistAuthorization = false;
        });
        app.MapGet("/swagger/{**path}", middleware.Build())
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme }).ExcludeFromDescription();

        var api = app.MapGroup("/api/v1").WithGroupName("v1").RequireAuthorization(ApiKeyAuthenticationHandler.PolicyName);
        api.AddEndpointFilter<ApiErrorFilter>();
        api.ProducesProblem(400).ProducesProblem(401).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);

        api.MapGet("/status", async (PresenterDashboardService dashboard, CancellationToken ct) => StatusResponse.From(await dashboard.GetAsync(ct)))
            .WithTags("Status").WithSummary("Aktuellen Zustand abrufen");
        api.MapGet("/videos", async (IMediaLibraryService library, CancellationToken ct) => (await library.GetAllAsync(ct)).Select(VideoResponse.From))
            .WithTags("Medien").WithSummary("Medien auflisten");
        api.MapPost("/videos/upload", UploadAsync).DisableAntiforgery().WithTags("Medien")
            .WithSummary("Video hochladen (Multipart-Feld video)").Produces<VideoResponse>(201);
        api.MapPost("/videos/set-enabled", async (VideoEnabledRequest request, IMediaLibraryService library, CancellationToken ct) =>
        {
            await RequireVideoAsync(request.Id, library, ct);
            await library.SetEnabledAsync(request.Id, request.Enabled, ct);
            return Results.NoContent();
        }).WithTags("Medien").Produces(204);
        api.MapPost("/videos/reanalyze", async (IdRequest request, IMediaLibraryService library, CancellationToken ct) =>
        {
            var id = checked((int)request.Id);
            await RequireVideoAsync(id, library, ct);
            await library.ReanalyzeAsync(id, ct);
            return Results.NoContent();
        }).WithTags("Medien").Produces(204);
        api.MapPost("/videos/remove-missing", async (IdRequest request, IMediaLibraryService library, IMediaCleanupService cleanup, CancellationToken ct) =>
        {
            var id = checked((int)request.Id);
            await RequireVideoAsync(id, library, ct);
            return new CleanupResponse(await cleanup.RemoveMissingAsync(id, ct));
        }).WithTags("Medien");
        api.MapPost("/videos/cleanup-missing", async (IMediaCleanupService cleanup, CancellationToken ct) => new CleanupResponse(await cleanup.RemoveMissingAsync(cancellationToken: ct)))
            .WithTags("Medien");
        api.MapGet("/videos/{mediaId:int}/preview", (Delegate)WebApplicationExtensions.GetMediaPreviewAsync).WithTags("Medien").Produces(200, contentType: "image/jpeg");

        MapPlayback(api);
        MapYouTube(api);
        MapNews(api);
    }

    private static void MapPlayback(RouteGroupBuilder api)
    {
        api.MapGet("/queue", async (PlaybackQueueService queue, CancellationToken ct) => (await queue.GetQueueAsync(ct)).Select(QueueResponse.From)).WithTags("Wiedergabe");
        api.MapGet("/history", async (PlaybackQueueService queue, CancellationToken ct) => (await queue.GetHistoryAsync(ct)).Select(HistoryResponse.From)).WithTags("Wiedergabe");
        api.MapPost("/queue/next", async (VideoPlaybackRequest request, IPlaybackCommandService playback, CancellationToken ct) =>
            QueueResponse.From(await playback.PlayNextAsync(request.MediaId, ApiTime.FromSeconds(request.StartSeconds), ApiTime.FromSeconds(request.DurationSeconds, true), ct)))
            .WithTags("Wiedergabe");
        api.MapPost("/queue/now", async (VideoPlaybackRequest request, IPlaybackCommandService playback, IPresenterTelemetry telemetry, CancellationToken ct) =>
            QueueResponse.From(await playback.PlayNowAsync(request.MediaId, telemetry.Current.Position, ApiTime.FromSeconds(request.StartSeconds), ApiTime.FromSeconds(request.DurationSeconds, true), ct)))
            .WithTags("Wiedergabe");
        api.MapPost("/queue/move-up", async (IdRequest request, PlaybackQueueService queue, CancellationToken ct) => { await queue.MoveAsync(request.Id, -1, ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/queue/move-down", async (IdRequest request, PlaybackQueueService queue, CancellationToken ct) => { await queue.MoveAsync(request.Id, 1, ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/queue/remove", async (IdRequest request, PlaybackQueueService queue, CancellationToken ct) => { await queue.RemoveAsync(request.Id, ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/queue/play-next", async (IdRequest request, IPlaybackCommandService playback, CancellationToken ct) => { await playback.PrioritizeQueuedAsync(request.Id, ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/queue/play-now", async (IdRequest request, IPlaybackCommandService playback, IPresenterTelemetry telemetry, CancellationToken ct) => QueueResponse.From(await playback.PlayQueuedNowAsync(request.Id, telemetry.Current.Position, ct))).WithTags("Wiedergabe");
        api.MapPost("/queue/regenerate", async (PlaybackQueueService queue, CancellationToken ct) => { await queue.RegenerateAsync(ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/history/clear", async (PlaybackQueueService queue, CancellationToken ct) => { await queue.ClearHistoryAsync(ct); return Results.NoContent(); }).WithTags("Wiedergabe").Produces(204);
        api.MapPost("/presenter/activate", async (IPresenterControlService presenter, CancellationToken ct) => { await presenter.ActivateAsync(ct); return Results.NoContent(); }).WithTags("Presenter").Produces(204);
        api.MapPost("/presenter/pause", async (IPresenterControlService presenter, CancellationToken ct) => { await presenter.PauseAsync(ct); return Results.NoContent(); }).WithTags("Presenter").Produces(204);
        api.MapPost("/presenter/hide", async (IPresenterControlService presenter, CancellationToken ct) => { await presenter.HideAsync(ct); return Results.NoContent(); }).WithTags("Presenter").Produces(204);
        api.MapPost("/presenter/stop", async (IPresenterControlService presenter, CancellationToken ct) => { await presenter.StopAsync(ct); return Results.NoContent(); }).WithTags("Presenter").Produces(204);
    }

    private static void MapYouTube(RouteGroupBuilder api)
    {
        api.MapGet("/youtube/reference", (string url) =>
        {
            var reference = YouTubeUrlParser.Parse(url);
            return new YouTubeReferenceResponse(reference.VideoId, reference.SourceKey, reference.CanonicalUrl);
        }).WithTags("YouTube");
        api.MapPost("/youtube/download", async (YouTubeRequest request, YouTubeDownloadCoordinator downloads, CancellationToken ct) => DownloadResponse.From(await downloads.StartAsync(request.Url, ct))).WithTags("YouTube");
        api.MapGet("/youtube/download/{videoId}", async (string videoId, YouTubeDownloadCoordinator downloads, CancellationToken ct) => DownloadResponse.From(await downloads.GetAsync(videoId, ct))).WithTags("YouTube");
        api.MapPost("/youtube/download/{videoId}/intent", async (string videoId, YouTubeIntentRequest request, YouTubeDownloadCoordinator downloads, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Action) || !Enum.IsDefined(request.Mode)) throw new ArgumentException("Invalid playback intent.");
            return DownloadResponse.From(await downloads.SetIntentAsync(videoId, new(request.Action, request.Mode,
                ApiTime.FromSeconds(request.StartSeconds), ApiTime.FromSeconds(request.DurationSeconds, true), ApiTime.FromSeconds(request.MaximumDurationSeconds, true)), ct));
        }).WithTags("YouTube");
        api.MapPost("/youtube/next", (YouTubeRequest request, YouTubeDownloadCoordinator downloads, CancellationToken ct) => YouTubeAsync(request, downloads, YouTubeDownloadAction.Next, ct)).WithTags("YouTube");
        api.MapPost("/youtube/now", (YouTubeRequest request, YouTubeDownloadCoordinator downloads, CancellationToken ct) => YouTubeAsync(request, downloads, YouTubeDownloadAction.Now, ct)).WithTags("YouTube");
    }

    private static async Task<DownloadResponse> YouTubeAsync(YouTubeRequest request, YouTubeDownloadCoordinator downloads, YouTubeDownloadAction action, CancellationToken ct)
    {
        var reference = YouTubeUrlParser.Parse(request.Url);
        var mode = request.PlaybackMode ?? (request.StartSeconds.HasValue || request.DurationSeconds.HasValue ? YouTubeDownloadMode.Custom :
            request.MaximumDurationSeconds.HasValue ? YouTubeDownloadMode.Automatic : YouTubeDownloadMode.Full);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Invalid playback mode.");
        return DownloadResponse.From(await downloads.SetIntentAsync(reference.VideoId, new(action, mode, ApiTime.FromSeconds(request.StartSeconds),
            ApiTime.FromSeconds(request.DurationSeconds, true), ApiTime.FromSeconds(request.MaximumDurationSeconds, true)), ct));
    }

    private static void MapNews(RouteGroupBuilder api)
    {
        api.MapGet("/news", async (INewsService news, CancellationToken ct) => (await news.GetAllAsync(ct)).Select(NewsResponse.From)).WithTags("News");
        api.MapGet("/news/display", async ([Microsoft.AspNetCore.Mvc.FromServices] INewsDisplayState state, CancellationToken ct) =>
        {
            var snapshot = await state.GetNewsDisplayAsync(ct);
            return new NewsDisplayResponse(snapshot.Main is null ? null : NewsResponse.From(snapshot.Main), snapshot.Ticker is null ? null : NewsResponse.From(snapshot.Ticker));
        }).WithTags("News");
        api.MapGet("/news/{id:long}", async (long id, INewsService news, CancellationToken ct) => NewsResponse.From(await RequireNewsAsync(id, news, ct))).WithTags("News");
        api.MapPost("/news", async (NewsRequest request, INewsService news, INewsCommandService commands, CancellationToken ct) =>
        {
            var item = await news.AddAsync(request.ToItem(), ct);
            var result = await CompleteNewsSaveAsync(item, request.ShowNow, commands, ct);
            return result.DisplayError is null ? Results.Created($"/api/v1/news/{item.Id}", result) : DisplayFailure(result);
        }).WithTags("News").Produces<NewsSaveResponse>(201);
        api.MapPut("/news/{id:long}", async (long id, NewsRequest request, INewsService news, INewsCommandService commands, CancellationToken ct) =>
        {
            var existing = await RequireNewsAsync(id, news, ct);
            var item = request.ToItem(id, existing.CreatedUtc);
            await news.UpdateAsync(item, ct);
            var result = await CompleteNewsSaveAsync(item, request.ShowNow, commands, ct);
            return result.DisplayError is null ? Results.Ok(result) : DisplayFailure(result);
        }).WithTags("News").Produces<NewsSaveResponse>();
        api.MapDelete("/news/{id:long}", async (long id, INewsService news, CancellationToken ct) =>
        {
            await RequireNewsAsync(id, news, ct);
            await news.DeleteAsync(id, ct);
            return Results.NoContent();
        }).WithTags("News").Produces(204);
        api.MapPost("/news/{id:long}/show", async (long id, INewsService news, INewsCommandService commands, CancellationToken ct) =>
            new NewsShowResponse(id, await commands.ShowNewsAndConfirmAsync(await RequireNewsAsync(id, news, ct), ct))).WithTags("News");
        api.MapPost("/news/stop", async (INewsCommandService commands, CancellationToken ct) => { await commands.StopNewsAsync(cancellationToken: ct); return Results.NoContent(); }).WithTags("News").Produces(204);
        api.MapPost("/news/stop-ticker", async (INewsCommandService commands, CancellationToken ct) => { await commands.StopTickerAsync(ct); return Results.NoContent(); }).WithTags("News").Produces(204);
    }

    internal static async Task<NewsSaveResponse> CompleteNewsSaveAsync(NewsItem item, bool showNow, INewsCommandService commands, CancellationToken ct)
    {
        if (!showNow) return new(NewsResponse.From(item), true, false, false);
        try { return new(NewsResponse.From(item), true, true, await commands.ShowNewsAndConfirmAsync(item, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return new(NewsResponse.From(item), true, true, false, "Die News wurde gespeichert, die Anzeige ist jedoch fehlgeschlagen."); }
    }

    private static IResult DisplayFailure(NewsSaveResponse result) => Results.Problem(statusCode: 503, title: result.DisplayError,
        extensions: new Dictionary<string, object?> { ["news"] = result.News, ["saved"] = true, ["displayRequested"] = true, ["displayed"] = false });

    internal static async Task<NewsItem> RequireNewsAsync(long id, INewsService news, CancellationToken ct) =>
        (await news.GetAllAsync(ct)).SingleOrDefault(item => item.Id == id) ?? throw new ApiNotFoundException();

    private static async Task RequireVideoAsync(int id, IMediaLibraryService library, CancellationToken ct)
    {
        if (await library.GetByIdAsync(id, ct) is null) throw new ApiNotFoundException();
    }

    private static async Task<IResult> UploadAsync(IFormFile video, IMediaLibraryService library, CancellationToken ct)
    {
        if (video.Length == 0 || !MediaFileSupport.IsSupported(Path.GetFileName(video.FileName))) throw new ArgumentException("Invalid upload.");
        await using var stream = video.OpenReadStream();
        var item = await library.AddUploadAsync(Path.GetFileName(video.FileName), stream, video.Length, ct);
        return Results.Created($"/api/v1/videos", VideoResponse.From(item));
    }
}

internal sealed class ApiNotFoundException : Exception;

internal sealed class ApiErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try { return await next(context); }
        catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (ApiNotFoundException) { return Results.Problem(statusCode: 404, title: "Eintrag nicht gefunden."); }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException or BadHttpRequestException)
        { return Results.Problem(statusCode: 400, title: "Ungültige Eingabe. Bitte die dokumentierten Felder prüfen."); }
        catch (InvalidOperationException) { return Results.Problem(statusCode: 409, title: "Die Aktion ist im aktuellen Zustand nicht möglich."); }
        catch (Exception exception)
        {
            context.HttpContext.RequestServices.GetRequiredService<ILogger<ApiErrorFilter>>().LogWarning("API action failed ({ErrorType})", exception.GetType().Name);
            return Results.Problem(statusCode: 503, title: "Die Aktion ist derzeit nicht verfügbar.");
        }
    }
}
