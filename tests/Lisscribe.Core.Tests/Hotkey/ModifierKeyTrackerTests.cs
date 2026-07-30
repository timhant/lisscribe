using Lisscribe.Core.Hotkey;
using Xunit;

namespace Lisscribe.Core.Tests.Hotkey;

public sealed class ModifierKeyTrackerTests
{
    [Fact]
    public void Current_Initially_IsNone()
    {
        Assert.Equal(ModifierKeys.None, new ModifierKeyTracker().Current);
    }

    [Fact]
    public void OnKeyDown_AccumulatesFlags()
    {
        var tracker = new ModifierKeyTracker();

        tracker.OnKeyDown(ModifierKeys.Ctrl);
        tracker.OnKeyDown(ModifierKeys.Alt);

        Assert.Equal(ModifierKeys.Ctrl | ModifierKeys.Alt, tracker.Current);
    }

    [Fact]
    public void OnKeyUp_ClearsOnlyThatFlag()
    {
        var tracker = new ModifierKeyTracker();
        tracker.OnKeyDown(ModifierKeys.Ctrl);
        tracker.OnKeyDown(ModifierKeys.Alt);

        tracker.OnKeyUp(ModifierKeys.Alt);

        Assert.Equal(ModifierKeys.Ctrl, tracker.Current);
    }

    [Fact]
    public void OnKeyDown_IsIdempotent()
    {
        // 按住不放时 Windows 会重复投递 KEYDOWN，不得把状态搞乱。
        var tracker = new ModifierKeyTracker();

        tracker.OnKeyDown(ModifierKeys.Ctrl);
        tracker.OnKeyDown(ModifierKeys.Ctrl);
        tracker.OnKeyUp(ModifierKeys.Ctrl);

        Assert.Equal(ModifierKeys.None, tracker.Current);
    }

    [Fact]
    public void OnKeyUp_ForKeyNeverPressed_IsHarmless()
    {
        var tracker = new ModifierKeyTracker();

        tracker.OnKeyUp(ModifierKeys.Shift);

        Assert.Equal(ModifierKeys.None, tracker.Current);
    }

    [Fact]
    public void Resync_OverwritesAccumulatedStateWholesale()
    {
        // 这是本类存在的核心理由：KEYUP 会在锁屏 / UAC / secure desktop / RDP 断开时丢失，
        // 表会永久失同步（Ctrl 卡在「按下」→ 单按 Space 就触发录音）。
        // 前台窗口变化、会话变化、电源事件、看门狗 tick 四处会调 Resync 把表拉回真实状态。
        var tracker = new ModifierKeyTracker();
        tracker.OnKeyDown(ModifierKeys.Ctrl);
        tracker.OnKeyDown(ModifierKeys.Alt);

        tracker.Resync(ModifierKeys.Shift);

        Assert.Equal(ModifierKeys.Shift, tracker.Current);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var tracker = new ModifierKeyTracker();
        tracker.OnKeyDown(ModifierKeys.Ctrl);

        tracker.Reset();

        Assert.Equal(ModifierKeys.None, tracker.Current);
    }
}
