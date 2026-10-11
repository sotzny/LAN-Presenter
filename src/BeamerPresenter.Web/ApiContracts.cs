using System.Text.Json.Serialization;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.Web;

public sealed record NewsRequest([property: JsonRequired] string Title, [property: JsonRequired] string Text,
    [property: JsonRequired] NewsMode Mode, double? DurationSeconds = null,
    bool Permanent = false, DateTimeOffset? ValidFrom = null, DateTimeOffset? ValidUntil = null, int Priority = 0, bool ShowNow = false)
{
    public NewsItem ToItem(long id = 0, DateTimeOffset createdUtc = default)
    {
        if (!Enum.IsDefined(Mode)) throw new ArgumentException("Invalid news mode.");
        return new NewsItem
        {
            Id = id,
            CreatedUtc = createdUtc,
            Title = Title?.Trim() ?? "",
            Text = Text?.Trim() ?? "",
            Mode = Mode,
            Duration = ApiTime.FromSeconds(DurationSeconds, positive: !Permanent),
            Permanent = Permanent,
            ValidFrom = ValidFrom,
            ValidUntil = ValidUntil,
            Priority = Priority
        };
    }
}

public sealed record NewsResponse(long Id, string Title, string Text, NewsMode Mode, double? DurationSeconds,
    bool Permanent, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil, int Priority, DateTimeOffset CreatedUtc)
{
    public static NewsResponse From(NewsItem item) => new(item.Id, item.Title, item.Text, item.Mode, item.Duration?.TotalSeconds,
        item.Permanent, item.ValidFrom, item.ValidUntil, item.Priority, item.CreatedUtc);
}

public sealed record NewsSaveResponse(NewsResponse News, bool Saved, bool DisplayRequested, bool Displayed, string? DisplayError = null);
public sealed record NewsDisplayResponse(NewsResponse? Main, NewsResponse? Ticker);
public sealed record NewsShowResponse(long Id, bool Displayed);
public sealed record IdRequest(long Id);
public sealed record VideoEnabledRequest(int Id, bool Enabled);
public sealed record VideoPlaybackRequest(int MediaId, double? StartSeconds = null, double? DurationSeconds = null);
public sealed record YouTubeRequest(string Url, double? StartSeconds = null, double? DurationSeconds = null,
    double? MaximumDurationSeconds = null, YouTubeDownloadMode? PlaybackMode = null);
public sealed record YouTubeIntentRequest(YouTubeDownloadAction Action, YouTubeDownloadMode Mode,
    double? StartSeconds = null, double? DurationSeconds = null, double? MaximumDurationSeconds = null);
public sealed record YouTubeReferenceResponse(string VideoId, string SourceKey, string CanonicalUrl);
public sealed record DownloadResponse(string VideoId, YouTubeDownloadPhase Phase, int? MediaId, string? Error, double? DurationSeconds)
{
    public static DownloadResponse From(YouTubeDownloadSnapshot item) => new(item.VideoId, item.Phase, item.MediaId,
        item.Error is null ? null : "Der YouTube-Download oder die Wiedergabe ist fehlgeschlagen.", item.Duration?.TotalSeconds);
}
public sealed record CleanupResponse(int Removed);
public sealed record VideoResponse(int Id, string FileName, long FileSize, bool Enabled, bool IsAvailable,
    MediaProbeStatus ProbeStatus, MediaPlaybackStatus PlaybackStatus, double? DurationSeconds, DateTimeOffset AddedAtUtc,
    string? Container, string? VideoCodec, string? AudioCodec, int? Width, int? Height, DateTimeOffset LastWriteUtc, DateTimeOffset? LastScannedUtc, string? YouTubeSourceKey)
{
    public static VideoResponse From(VideoAsset item) => new(item.Id, item.FileName, item.FileSize, item.Enabled, item.IsAvailable,
        item.ProbeStatus, item.PlaybackStatus, item.Duration?.TotalSeconds, item.AddedAtUtc,
        item.Container, item.VideoCodec, item.AudioCodec, item.VideoWidth, item.VideoHeight, item.LastWriteUtc, item.LastScannedUtc, item.YouTubeSourceKey);
}
public sealed record QueueResponse(long Id, MediaSourceType SourceType, int? MediaId, string? ExternalSourceKey,
    double StartSeconds, double EndSeconds, QueueEntryOrigin Origin, int SortOrder, QueueEntryStatus Status,
    DateTimeOffset CreatedUtc, DateTimeOffset? StartedUtc, DateTimeOffset? CompletedUtc)
{
    public static QueueResponse From(QueueEntry item) => new(item.Id, item.SourceType, item.MediaId, item.ExternalSourceKey,
        item.StartPosition.TotalSeconds, item.EndPosition.TotalSeconds, item.Origin, item.SortOrder, item.Status,
        item.CreatedUtc, item.StartedUtc, item.CompletedUtc);
}
public sealed record HistoryResponse(long Id, int? MediaId, string? ExternalSourceKey, MediaSourceType SourceType,
    double PlannedStartSeconds, double PlannedEndSeconds, double? ActualStartSeconds, double? ActualEndSeconds,
    DateTimeOffset StartedUtc, DateTimeOffset? FinishedUtc, bool Completed, bool Interrupted, PlaybackReason PlaybackReason)
{
    public static HistoryResponse From(PlaybackHistory item) => new(item.Id, item.MediaId, item.ExternalSourceKey, item.SourceType,
        item.PlannedStart.TotalSeconds, item.PlannedEnd.TotalSeconds, item.ActualStart?.TotalSeconds, item.ActualEnd?.TotalSeconds,
        item.StartedUtc, item.FinishedUtc, item.Completed, item.Interrupted, item.PlaybackReason);
}
public sealed record StatusResponse(string PresenterState, bool BrowserConnected, string BrowserStatus, DateTimeOffset LastHeartbeatUtc,
    string CurrentTitle, double? PositionSeconds, double? DurationSeconds, bool FfprobeAvailable, bool MediaScannerRunning,
    DateTimeOffset? LastMediaScanUtc, int FileCount, int PlayableCount, int ErrorCount, int QueueCount, DateTimeOffset GeneratedUtc)
{
    public static StatusResponse From(PresenterDashboardSnapshot item) => new(item.PresenterState, item.BrowserConnected,
        item.BrowserStatus, item.LastHeartbeatUtc, item.CurrentTitle, item.Position?.TotalSeconds, item.Duration?.TotalSeconds,
        item.FfprobeAvailable, item.MediaScannerRunning, item.LastMediaScanUtc, item.FileCount, item.PlayableCount, item.ErrorCount,
        item.QueueCount, item.GeneratedUtc);
}

internal static class ApiTime
{
    public static TimeSpan? FromSeconds(double? seconds, bool positive = false)
    {
        if (seconds is null) return null;
        if (!double.IsFinite(seconds.Value) || seconds < 0 || (positive && seconds <= 0) || seconds > TimeSpan.MaxValue.TotalSeconds / 2)
            throw new ArgumentException("Invalid duration or position.");
        return TimeSpan.FromSeconds(seconds.Value);
    }
}
