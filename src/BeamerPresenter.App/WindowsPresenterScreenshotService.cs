using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using BeamerPresenter.Application;

namespace BeamerPresenter.App;

internal interface IMonitorScreenshotCapture
{
    byte[] Capture(DisplayMonitor monitor);
}

internal sealed class WindowsPresenterScreenshotService(
    IPresenterSettingsService settings,
    IMonitorService monitors,
    IMonitorScreenshotCapture capture,
    TimeProvider clock) : IPresenterScreenshotService, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private DisplayMonitor? cachedMonitor;
    private PresenterScreenshot? cached;

    public async Task<PresenterScreenshot> CaptureAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var configuration = await settings.GetAsync(cancellationToken);
            var monitor = monitors.GetAll().FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceName, configuration.MonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (monitor is null)
                throw new PresenterScreenshotUnavailableException("Der konfigurierte Beamer-Monitor ist nicht verfügbar.");
            var now = clock.GetUtcNow();
            if (monitor == cachedMonitor && cached is not null && now >= cached.CapturedAtUtc &&
                now - cached.CapturedAtUtc < TimeSpan.FromSeconds(1)) return cached;

            cached = null;
            cachedMonitor = null;
            cancellationToken.ThrowIfCancellationRequested();
            var jpeg = capture.Capture(monitor);
            cached = new PresenterScreenshot(jpeg, clock.GetUtcNow());
            cachedMonitor = monitor;
            return cached;
        }
        catch
        {
            cached = null;
            cachedMonitor = null;
            throw;
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}

internal sealed class WindowsMonitorScreenshotCapture : IMonitorScreenshotCapture
{
    internal static Size PreviewSize(DisplayMonitor monitor)
    {
        var width = Math.Min(monitor.Width, 1280);
        return new Size(width, Math.Max(1, (int)Math.Round((double)monitor.Height * width / monitor.Width)));
    }

    public byte[] Capture(DisplayMonitor monitor)
    {
        using var original = new Bitmap(monitor.Width, monitor.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(original))
            graphics.CopyFromScreen(monitor.X, monitor.Y, 0, 0, original.Size, CopyPixelOperation.SourceCopy);
        return EncodePreview(original, monitor);
    }

    internal static byte[] EncodePreview(Bitmap original, DisplayMonitor monitor)
    {
        var size = PreviewSize(monitor);
        using var preview = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(preview))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            graphics.DrawImage(original, new Rectangle(Point.Empty, preview.Size), 0, 0,
                original.Width, original.Height, GraphicsUnit.Pixel, attributes);
        }
        using var stream = new MemoryStream();
        preview.Save(stream, ImageFormat.Jpeg);
        return stream.ToArray();
    }
}
