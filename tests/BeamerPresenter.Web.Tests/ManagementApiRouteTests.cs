using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using BeamerPresenter.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Web.Tests;

public sealed partial class AuthenticatedWebRouteTests
{
    private static readonly JsonSerializerOptions ApiJsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string TestApiKey = "api-integration-key-with-at-least-32-characters";

    private async Task<HttpClient> ApiClientAsync()
    {
        await _application!.Services.GetRequiredService<IApiKeyService>().SetKeyAsync(TestApiKey);
        var client = _application.GetTestClient();
        client.DefaultRequestHeaders.Add("X-API-Key", TestApiKey);
        return client;
    }

    [Fact]
    public async Task Api_key_is_required_even_with_cookie_and_cannot_access_management_pages()
    {
        using var client = _application!.GetTestClient();
        using var missing = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Null(missing.Headers.Location);
        client.DefaultRequestHeaders.Add("Cookie", await LoginAsync(client));
        using var cookieOnly = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        using var api = await ApiClientAsync();
        foreach (var path in new[] { "/developer", "/swagger/index.html", "/news", "/updates" })
        {
            using var response = await api.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/login", response.Headers.Location!.OriginalString);
        }
        using var keyManagement = await api.PostAsync("/developer/key/disable", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, keyManagement.StatusCode);
    }

    [Fact]
    public async Task Wrong_duplicate_rotated_and_disabled_keys_fail_immediately()
    {
        using var client = await ApiClientAsync();
        using var valid = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", new[] { TestApiKey, TestApiKey });
        using var duplicate = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, duplicate.StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", "wrong");
        using var wrong = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", TestApiKey);
        var keys = _application!.Services.GetRequiredService<IApiKeyService>();
        await keys.SetKeyAsync(TestApiKey + "-new");
        using var rotated = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, rotated.StatusCode);
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("X-API-Key", TestApiKey + "-new");
        using var newKey = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.OK, newKey.StatusCode);
        await keys.DisableAsync();
        using var disabled = await client.GetAsync("/api/v1/news");
        Assert.Equal(HttpStatusCode.Unauthorized, disabled.StatusCode);
    }

    [Fact]
    public async Task Swagger_and_openapi_have_separate_access_and_document_only_versioned_api()
    {
        using var client = await ApiClientAsync();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = document.RootElement.GetProperty("paths");
        Assert.All(paths.EnumerateObject(), path => Assert.StartsWith("/api/v1/", path.Name));
        Assert.True(paths.TryGetProperty("/api/v1/news/{id}", out var news));
        Assert.True(news.TryGetProperty("put", out _));
        Assert.False(paths.TryGetProperty("/api/v1/updates/install", out _));
        Assert.Equal("X-API-Key", document.RootElement.GetProperty("components").GetProperty("securitySchemes").GetProperty("ApiKey").GetProperty("name").GetString());
        client.DefaultRequestHeaders.Remove("X-API-Key");
        client.DefaultRequestHeaders.Add("Cookie", await LoginAsync(client));
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
        Assert.Contains("swagger-ui", await ui.Content.ReadAsStringAsync());
        using var asset = await client.GetAsync("/swagger/swagger-ui-bundle.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        using var cookieDocument = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, cookieDocument.StatusCode);
    }

    [Fact]
    public async Task News_create_edit_read_show_and_delete_preserve_identity_and_report_display_outcome()
    {
        using var client = await ApiClientAsync();
        var request = new NewsRequest("Titel", "Text", NewsMode.Ticker, 120, ValidFrom: DateTimeOffset.UtcNow.AddHours(1), Priority: 4);
        using var created = await client.PostAsJsonAsync("/api/v1/news", request, ApiJsonOptions);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var result = (await created.Content.ReadFromJsonAsync<NewsSaveResponse>(ApiJsonOptions))!;
        Assert.True(result.Saved);
        Assert.False(result.DisplayRequested);
        Assert.Empty(_playbackCommands.ShownNews);
        var id = result.News.Id;
        using var updated = await client.PutAsJsonAsync($"/api/v1/news/{id}", request with { Title = "Bearbeitet", ShowNow = true, Mode = NewsMode.Fullscreen }, ApiJsonOptions);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        var edited = (await updated.Content.ReadFromJsonAsync<NewsSaveResponse>(ApiJsonOptions))!;
        Assert.Equal(result.News.CreatedUtc, edited.News.CreatedUtc);
        Assert.Equal(id, edited.News.Id);
        Assert.True(edited.Displayed);
        using var display = await client.GetAsync("/api/v1/news/display");
        Assert.Equal(HttpStatusCode.OK, display.StatusCode);
        using var read = await client.GetAsync($"/api/v1/news/{id}");
        Assert.Contains("Bearbeitet", await read.Content.ReadAsStringAsync());
        _playbackCommands.ConfirmDisplay = false;
        using var blocked = await client.PostAsync($"/api/v1/news/{id}/show", null);
        Assert.False((await blocked.Content.ReadFromJsonAsync<NewsShowResponse>(ApiJsonOptions))!.Displayed);
        _playbackCommands.FailDisplay = true;
        using var failed = await client.PutAsJsonAsync($"/api/v1/news/{id}", request with { Title = "Gespeichert", ShowNow = true }, ApiJsonOptions);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        using var failure = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        Assert.True(failure.RootElement.GetProperty("saved").GetBoolean());
        Assert.Equal(id, failure.RootElement.GetProperty("news").GetProperty("id").GetInt64());
        Assert.DoesNotContain("Internal path", await failed.Content.ReadAsStringAsync());
        using var deleted = await client.DeleteAsync($"/api/v1/news/{id}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync($"/api/v1/news/{id}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Theory]
    [InlineData("{\"title\":\"\",\"text\":\"Text\",\"mode\":\"Ticker\",\"permanent\":true}")]
    [InlineData("{\"title\":\"Titel\",\"text\":\"Text\",\"mode\":\"Invalid\",\"permanent\":true}")]
    [InlineData("{\"title\":\"Titel\",\"text\":\"Text\",\"mode\":0,\"permanent\":true}")]
    [InlineData("{\"title\":\"Titel\",\"text\":\"Text\",\"mode\":\"Ticker\",\"durationSeconds\":-1}")]
    [InlineData("{\"title\":\"Titel\",\"text\":\"Text\",\"mode\":\"Ticker\"}")]
    public async Task News_invalid_json_contract_returns_bad_request(string json)
    {
        using var client = await ApiClientAsync();
        using var response = await client.PostAsync("/api/v1/news", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await _application!.Services.GetRequiredService<INewsService>().GetAllAsync());
    }

    [Fact]
    public async Task Developer_requires_antiforgery_and_generated_key_is_not_repeated_on_page()
    {
        using var client = _application!.GetTestClient();
        var cookie = await LoginAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        using var forged = await client.PostAsync("/developer/key/generate", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        var token = await AddAntiforgeryCookieAsync(client, cookie, "/developer");
        using var generated = await client.PostAsync("/developer/key/generate", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.OK, generated.StatusCode);
        Assert.Contains("no-store", generated.Headers.CacheControl!.ToString());
        var key = Regex.Match(await generated.Content.ReadAsStringAsync(), "id=\"generated-api-key\" value=\"([^\"]+)\"").Groups[1].Value;
        Assert.Equal(64, key.Length);
        Assert.True(await _application!.Services.GetRequiredService<IApiKeyService>().VerifyAsync(key));
        using var page = await client.GetAsync("/developer");
        Assert.DoesNotContain(key, await page.Content.ReadAsStringAsync());
        using var disabled = await client.PostAsync("/developer/key/disable", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.Redirect, disabled.StatusCode);
        Assert.False(await _application!.Services.GetRequiredService<IApiKeyService>().HasKeyAsync());
    }

    [Fact]
    public async Task Web_news_editor_prefills_and_updates_without_showing_unless_requested()
    {
        var service = _application!.Services.GetRequiredService<INewsService>();
        var item = await service.AddAsync(new NewsItem { Title = "Original", Text = "Text", Mode = NewsMode.Ticker, Permanent = true });
        using var client = _application.GetTestClient();
        var cookie = await LoginAsync(client);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var token = await AddAntiforgeryCookieAsync(client, cookie, $"/news?edit={item.Id}");
        var fields = new Dictionary<string, string>
        {
            ["id"] = item.Id.ToString(),
            ["title"] = "Bearbeitet",
            ["text"] = "Neuer Text",
            ["mode"] = "Fullscreen",
            ["duration"] = "00:02:00",
            ["priority"] = "4",
            ["__RequestVerificationToken"] = token
        };
        using var saved = await client.PostAsync("/api/news/update", new FormUrlEncodedContent(fields));
        Assert.Equal("/news?news=updated", saved.Headers.Location!.OriginalString);
        Assert.Empty(_playbackCommands.ShownNews);
        var edited = Assert.Single(await service.GetAllAsync());
        Assert.Equal(item.CreatedUtc, edited.CreatedUtc);
        Assert.Equal("Bearbeitet", edited.Title);
        fields["showNow"] = "true";
        using var shown = await client.PostAsync("/api/news/update", new FormUrlEncodedContent(fields));
        Assert.Equal("/news?news=updated-shown", shown.Headers.Location!.OriginalString);
        Assert.Single(_playbackCommands.ShownNews);
    }

    [Fact]
    public async Task Api_media_upload_library_changes_reanalysis_and_cleanup_use_real_storage()
    {
        var directory = Path.Combine(_dataDirectory, "Uploads");
        Directory.CreateDirectory(directory);
        await _application!.Services.GetRequiredService<IMediaFolderService>().AddAsync(directory, false);
        using var client = await ApiClientAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0, 1, 2, 3]), "video", "../../upload.mp4");
        using var upload = await client.PostAsync("/api/v1/videos/upload", form);
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var video = (await upload.Content.ReadFromJsonAsync<VideoResponse>(ApiJsonOptions))!;
        Assert.Equal("upload.mp4", video.FileName);
        Assert.True(File.Exists(Path.Combine(directory, "upload.mp4")));
        using var list = await client.GetAsync("/api/v1/videos");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var json = await list.Content.ReadAsStringAsync();
        Assert.DoesNotContain("fullPath", json);
        Assert.DoesNotContain(directory.Replace("\\", "\\\\"), json);
        using var disable = await client.PostAsJsonAsync("/api/v1/videos/set-enabled", new VideoEnabledRequest(video.Id, false));
        Assert.Equal(HttpStatusCode.NoContent, disable.StatusCode);
        Assert.False((await _application.Services.GetRequiredService<IMediaLibraryService>().GetByIdAsync(video.Id))!.Enabled);
        using var reanalyze = await client.PostAsJsonAsync("/api/v1/videos/reanalyze", new IdRequest(video.Id));
        Assert.Equal(HttpStatusCode.NoContent, reanalyze.StatusCode);
        using var preview = await client.GetAsync($"/api/v1/videos/{video.Id}/preview");
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        using var unknown = await client.PostAsJsonAsync("/api/v1/videos/set-enabled", new VideoEnabledRequest(int.MaxValue, true));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var invalid = new MultipartFormDataContent();
        invalid.Add(new ByteArrayContent([1]), "video", "bad.exe");
        using var rejected = await client.PostAsync("/api/v1/videos/upload", invalid);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var empty = new MultipartFormDataContent();
        empty.Add(new ByteArrayContent([]), "video", "empty.mp4");
        using var emptyResponse = await client.PostAsync("/api/v1/videos/upload", empty);
        Assert.Equal(HttpStatusCode.BadRequest, emptyResponse.StatusCode);
        File.Delete(Path.Combine(directory, "upload.mp4"));
        using var remove = await client.PostAsJsonAsync("/api/v1/videos/remove-missing", new IdRequest(video.Id));
        Assert.Equal(1, (await remove.Content.ReadFromJsonAsync<CleanupResponse>())!.Removed);
        using var cleanup = await client.PostAsync("/api/v1/videos/cleanup-missing", null);
        Assert.Equal(0, (await cleanup.Content.ReadFromJsonAsync<CleanupResponse>())!.Removed);
        using var status = await client.GetAsync("/api/v1/status");
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.DoesNotContain("apiKey", await status.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Api_playback_actions_preserve_pending_entries_and_use_reported_position()
    {
        using var client = await ApiClientAsync();
        var store = _application!.Services.GetRequiredService<IPlaybackStore>();
        var first = await store.AddQueueEntryAsync(CreatePendingQueueEntry(1, QueueEntryOrigin.Manual));
        var second = await store.AddQueueEntryAsync(CreatePendingQueueEntry(2, QueueEntryOrigin.Manual));
        var playing = CreatePendingQueueEntry(0, QueueEntryOrigin.Automatic);
        playing.Status = QueueEntryStatus.Playing;
        playing = await store.AddQueueEntryAsync(playing);
        var state = _application.Services.GetRequiredService<PresenterConnectionState>();
        state.Report("Playing", 99.5, 300, null);
        using var next = await client.PostAsJsonAsync("/api/v1/queue/next", new VideoPlaybackRequest(42, 20, 90));
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(_playbackCommands.NextCalls).Start);
        using var now = await client.PostAsJsonAsync("/api/v1/queue/now", new VideoPlaybackRequest(42, 20, 90));
        Assert.Equal(HttpStatusCode.OK, now.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(99.5), _playbackCommands.LastCurrentPosition);
        using var queuedNow = await client.PostAsJsonAsync("/api/v1/queue/play-now", new IdRequest(first.Id));
        Assert.Equal(HttpStatusCode.OK, queuedNow.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(99.5), _playbackCommands.LastCurrentPosition);
        using var prioritize = await client.PostAsJsonAsync("/api/v1/queue/play-next", new IdRequest(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, prioritize.StatusCode);
        Assert.Contains(second.Id, _playbackCommands.PrioritizedQueueEntries);
        using var up = await client.PostAsJsonAsync("/api/v1/queue/move-up", new IdRequest(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, up.StatusCode);
        Assert.Equal(new[] { second.Id, first.Id }, (await store.GetQueueAsync()).Where(item => item.Status == QueueEntryStatus.Pending).Select(item => item.Id));
        using var down = await client.PostAsJsonAsync("/api/v1/queue/move-down", new IdRequest(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, down.StatusCode);
        using var conflict = await client.PostAsJsonAsync("/api/v1/queue/remove", new IdRequest(playing.Id));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var regenerate = await client.PostAsync("/api/v1/queue/regenerate", null);
        Assert.Equal(HttpStatusCode.NoContent, regenerate.StatusCode);
        Assert.Contains((await store.GetQueueAsync()), item => item.Id == playing.Id);
        Assert.Contains((await store.GetQueueAsync()), item => item.Id == first.Id);
        using var remove = await client.PostAsJsonAsync("/api/v1/queue/remove", new IdRequest(second.Id));
        Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        using var queue = await client.GetAsync("/api/v1/queue");
        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        using var history = await client.GetAsync("/api/v1/history");
        Assert.Equal(HttpStatusCode.OK, history.StatusCode);
        using var clear = await client.PostAsync("/api/v1/history/clear", null);
        Assert.Equal(HttpStatusCode.NoContent, clear.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/v1/queue/next", new VideoPlaybackRequest(42, -1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Api_presenter_news_stop_and_youtube_actions_dispatch_to_existing_services()
    {
        using var client = await ApiClientAsync();
        foreach (var action in new[] { "activate", "pause", "hide", "stop" })
        {
            using var response = await client.PostAsync("/api/v1/presenter/" + action, null);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(4, _presenterControls.ActivateCalls + _presenterControls.PauseCalls + _presenterControls.HideCalls + _presenterControls.StopCalls);
        _presenterControls.Failure = new IOException("Private error details");
        using var unavailable = await client.PostAsync("/api/v1/presenter/activate", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        Assert.DoesNotContain("Private error", await unavailable.Content.ReadAsStringAsync());
        using var mainStop = await client.PostAsync("/api/v1/news/stop", null);
        using var tickerStop = await client.PostAsync("/api/v1/news/stop-ticker", null);
        Assert.Equal(HttpStatusCode.NoContent, mainStop.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, tickerStop.StatusCode);
        Assert.Equal(1, _playbackCommands.StopNewsCalls);
        Assert.Equal(1, _playbackCommands.StopTickerCalls);
        using var reference = await client.GetAsync("/api/v1/youtube/reference?url=https://youtu.be/dQw4w9WgXcQ");
        Assert.Equal(HttpStatusCode.OK, reference.StatusCode);
        Assert.Equal("youtube:dQw4w9WgXcQ", (await reference.Content.ReadFromJsonAsync<YouTubeReferenceResponse>())!.SourceKey);
        using var badUrl = await client.GetAsync("/api/v1/youtube/reference?url=https://youtube.com.evil.test/watch?v=dQw4w9WgXcQ");
        Assert.Equal(HttpStatusCode.BadRequest, badUrl.StatusCode);
        using var download = await client.PostAsJsonAsync("/api/v1/youtube/download", new YouTubeRequest("https://youtu.be/dQw4w9WgXcQ"), ApiJsonOptions);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        using var downloadStatus = await client.GetAsync("/api/v1/youtube/download/dQw4w9WgXcQ");
        Assert.Equal(HttpStatusCode.OK, downloadStatus.StatusCode);
        using var intent = await client.PostAsJsonAsync("/api/v1/youtube/download/dQw4w9WgXcQ/intent",
            new YouTubeIntentRequest(YouTubeDownloadAction.Next, YouTubeDownloadMode.Automatic, MaximumDurationSeconds: 120), ApiJsonOptions);
        Assert.Equal(HttpStatusCode.OK, intent.StatusCode);
        foreach (var action in new[] { "next", "now" })
        {
            using var response = await client.PostAsJsonAsync("/api/v1/youtube/" + action,
                new YouTubeRequest("https://youtu.be/dQw4w9WgXcQ", MaximumDurationSeconds: 120), ApiJsonOptions);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    private static async Task<string> AddAntiforgeryCookieAsync(HttpClient client, string loginCookie, string path)
    {
        using var page = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        Assert.NotEmpty(token);
        var antiCookie = string.Join("; ", page.Headers.GetValues("Set-Cookie").Select(value => value.Split(';')[0]));
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", loginCookie + "; " + antiCookie);
        return token;
    }
}
