using BeamerPresenter.Web;

namespace BeamerPresenter.Web.Tests;

public sealed class PresenterConnectionStateTests
{
    [Theory]
    [InlineData("Playing")]
    [InlineData("Paused")]
    [InlineData("Ready")]
    public void Heartbeat_refreshes_position_and_time_without_replacing_playback_status(string status)
    {
        var state = new PresenterConnectionState();
        state.Report(status, 12, 90, null);
        var timestamp = state.LatestReport.ReceivedUtc;
        Assert.False(state.Report("Heartbeat", 13, 90, null));
        Assert.Equal(status, state.Current.Status);
        Assert.Equal(TimeSpan.FromSeconds(13), state.Current.Position);
        Assert.True(state.Current.ReceivedUtc >= timestamp);
    }

    [Theory]
    [InlineData("Ended")]
    [InlineData("Error")]
    public void Heartbeat_does_not_allow_a_duplicate_terminal_transition(string terminal)
    {
        var state = new PresenterConnectionState();
        Assert.True(state.Report(terminal, 89, 90, "terminal"));
        Assert.False(state.Report("Heartbeat", 89, 90, null));
        Assert.Equal(terminal, state.Current.Status);
        Assert.Equal("terminal", state.Current.Message);
        Assert.False(state.Report(terminal, 89, 90, "terminal"));
        Assert.False(state.Report("Playing", 0, 90, null));
        Assert.True(state.Report(terminal, 89, 90, "terminal"));
    }
}
