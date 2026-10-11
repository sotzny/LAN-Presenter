using BeamerPresenter.App;
using BeamerPresenter.Application;
using BeamerPresenter.Domain;

namespace BeamerPresenter.App.Tests;

public sealed class WindowsPresenterScreenshotServiceTests
{
    [Fact]
    public async Task Selects_configured_monitor_with_negative_coordinates_and_shares_fresh_capture()
    {
        var settings = new Settings { Value = new() { MonitorDeviceName = "display2" } };
        var monitors = new Monitors();
        var capture = new FakeCapture();
        var clock = new Clock();
        using var service = new WindowsPresenterScreenshotService(settings, monitors, capture, clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => service.CaptureAsync())));
        Assert.Equal(1, capture.Calls);
        Assert.All(results, result => Assert.Same(results[0], result));
        Assert.Equal(monitors.Values[1], capture.Last);
        Assert.Equal(-1920, capture.Last!.X);
        Assert.Equal(clock.Now, results[0].CapturedAtUtc);
        clock.Now += TimeSpan.FromSeconds(1);
        Assert.NotSame(results[0], await service.CaptureAsync());
        Assert.Equal(2, capture.Calls);
    }

    [Fact]
    public async Task Monitor_changes_disconnects_and_capture_errors_invalidate_cached_image()
    {
        var settings = new Settings { Value = new() { MonitorDeviceName = "DISPLAY2" } };
        var monitors = new Monitors();
        var capture = new FakeCapture();
        var clock = new Clock();
        using var service = new WindowsPresenterScreenshotService(settings, monitors, capture, clock);
        var first = await service.CaptureAsync();
        settings.Value.MonitorDeviceName = "DISPLAY1";
        Assert.NotSame(first, await service.CaptureAsync());
        monitors.Values = [];
        await Assert.ThrowsAsync<PresenterScreenshotUnavailableException>(() => service.CaptureAsync());
        monitors.Values = [new("DISPLAY1", "Primary", 0, 0, 1920, 1080, true)];
        capture.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CaptureAsync());
        capture.Fail = false;
        await service.CaptureAsync();
        Assert.Equal(4, capture.Calls);
        monitors.Values = [monitors.Values[0] with { Width = 1280 }];
        await service.CaptureAsync();
        Assert.Equal(5, capture.Calls);
        settings.Value.MonitorDeviceName = null;
        await Assert.ThrowsAsync<PresenterScreenshotUnavailableException>(() => service.CaptureAsync());
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(800, 600)]
    public void Encodes_a_decodable_jpeg_with_the_expected_dimensions_and_pixels(int width, int height)
    {
        using var original = new Bitmap(width, height);
        using (var graphics = Graphics.FromImage(original)) graphics.Clear(Color.Red);
        var monitor = new DisplayMonitor("DISPLAY", "Test", -1920, -100, width, height, false);
        var bytes = WindowsMonitorScreenshotCapture.EncodePreview(original, monitor);
        Assert.Equal(0xff, bytes[0]);
        Assert.Equal(0xd8, bytes[1]);
        using var stream = new MemoryStream(bytes);
        using var decoded = new Bitmap(stream);
        Assert.Equal(WindowsMonitorScreenshotCapture.PreviewSize(monitor), decoded.Size);
        Assert.InRange(decoded.GetPixel(0, 0).R, 250, 255);
        Assert.InRange(decoded.GetPixel(0, 0).G, 0, 5);
    }

    [Theory]
    [InlineData(1920, 1080, 1280, 720)]
    [InlineData(800, 600, 800, 600)]
    [InlineData(3840, 2160, 1280, 720)]
    [InlineData(1080, 1920, 1080, 1920)]
    public void Preview_preserves_aspect_ratio_without_upscaling(int width, int height, int expectedWidth, int expectedHeight)
    {
        var result = WindowsMonitorScreenshotCapture.PreviewSize(new("DISPLAY", "Monitor", 0, 0, width, height, true));
        Assert.Equal(new Size(expectedWidth, expectedHeight), result);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeCapture : IMonitorScreenshotCapture
    {
        public int Calls;
        public DisplayMonitor? Last;
        public bool Fail;
        public byte[] Capture(DisplayMonitor monitor)
        {
            Calls++;
            Last = monitor;
            if (Fail) throw new InvalidOperationException("Capture failed");
            return [0xff, 0xd8, 0xff, 0xd9];
        }
    }

    private sealed class Monitors : IMonitorService
    {
        public IReadOnlyList<DisplayMonitor> Values = [
            new("DISPLAY1", "Primary", 0, 0, 1920, 1080, true),
            new("DISPLAY2", "Beamer", -1920, -100, 1920, 1080, false)];
        public IReadOnlyList<DisplayMonitor> GetAll() => Values;
    }

    private sealed class Settings : IPresenterSettingsService
    {
        public PresenterSettings Value = new();
        public Task<PresenterSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(Value);
        public Task SaveAsync(PresenterSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> VerifyWebPasswordAsync(string password, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> HasWebPasswordAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}
