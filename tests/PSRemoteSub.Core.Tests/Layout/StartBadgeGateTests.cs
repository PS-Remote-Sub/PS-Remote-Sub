using PSRemoteSub.Core.Layout;

namespace PSRemoteSub.Core.Tests.Layout;

public class StartBadgeGateTests
{
    [Fact]
    public void First_time_the_window_is_found_and_running_triggers_once()
    {
        var gate = new StartBadgeGate();
        Assert.False(gate.Update(false, false, true));
        Assert.True(gate.Update(true, false, true));
        Assert.False(gate.Update(true, false, true));
    }

    [Fact]
    public void Resume_from_pause_triggers_and_pause_does_not()
    {
        var gate = new StartBadgeGate();
        gate.Update(true, false, true);
        Assert.False(gate.Update(true, true, true));
        Assert.False(gate.Active);
        Assert.True(gate.Update(true, false, true));
    }

    [Fact]
    public void Overlay_turned_back_on_triggers()
    {
        var gate = new StartBadgeGate();
        gate.Update(true, false, true);
        Assert.False(gate.Update(true, false, false));
        Assert.True(gate.Update(true, false, true));
    }

    [Fact]
    public void Window_found_while_paused_triggers_on_resume()
    {
        var gate = new StartBadgeGate();
        Assert.False(gate.Update(true, true, true));
        Assert.True(gate.Update(true, false, true));
    }

    [Fact]
    public void Window_coming_back_after_it_was_lost_does_not_trigger_again()
    {
        var gate = new StartBadgeGate();
        gate.Update(true, false, true);
        Assert.False(gate.Update(false, false, true));
        Assert.False(gate.Update(true, false, true));
    }

    [Fact]
    public void Window_returning_while_a_resume_is_pending_still_waits_for_it()
    {
        var gate = new StartBadgeGate();
        gate.Update(true, false, true);
        gate.Update(true, true, true);
        gate.Update(false, true, true);
        Assert.False(gate.Update(true, true, true));
        Assert.True(gate.Update(true, false, true));
    }
}
