namespace BeamerPresenter.Application;

public interface IPresenterScreenshotService
{
    Task<PresenterScreenshot> CaptureAsync(CancellationToken cancellationToken = default);
}

public sealed record PresenterScreenshot(byte[] Jpeg, DateTimeOffset CapturedAtUtc);

public sealed class PresenterScreenshotUnavailableException(string message) : Exception(message);
