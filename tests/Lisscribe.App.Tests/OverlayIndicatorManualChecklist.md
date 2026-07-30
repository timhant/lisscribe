# 悬浮指示器手工验收清单

**先说这份清单不包含什么。**「悬浮窗真实观感」曾整组留成手工，理由是「单测结构上验不出样式
是否生效——它读的和写的是同一份窗口数据」。其中**能用独立预言机验的都已搬成自动化**，
下表所列因此**不在**本清单里：

| 已自动化的行为 | 测试 | 预言机 |
|---|---|---|
| D1 点击穿透：鼠标落到下方窗口 | `TheMouseGoesThroughTheOverlay_ToWhateverIsUnderneath` | 真光标 + 两层窗口各自数 `PointerMoved` |
| D1 的阳性对照：不设样式就挡住 | `WithoutTheClickThroughStyles_TheOverlayWouldSwallowTheMouse` | 同上，几何相同只是不设样式 |
| D2 不抢焦点 | `ShowingTheOverlay_DoesNotStealTheForegroundWindow` | `GetForegroundWindow`，带「本进程抢得动前台」的阳性对照 |
| 分层样式没把窗口弄没 | `TheLayeredStyle_DoesNotMakeTheOverlayInvisible` | `GetPixel` 直接读屏幕 |
| 四个扩展位真的置上了 | `TheOverlay_CarriesAllFourClickThroughStylesOnceItIsShown` | `GetWindowLongPtr`，带阴性对照 |
| **`Show()` 会抹掉样式，故每次都要重设** | `EveryShow_WipesTheStyles_SoTheyMustBeReAppliedEveryTime` | 同上，阳性对照在同一条用例里 |
| `Screens.Changed` 只订一次、关窗退干净 | `ClosingTheOverlay_UnsubscribesFromTheScreenChangedEvent` | 反射读 Avalonia 自己的 `_changedHandlers` 调用列表 |
| 落点在工作区内、边距正好 16px | `Reposition_LeavesExactlyTheSpecifiedMarginToTheBottomRightOfTheWorkArea` | `GetWindowRect` + `GetMonitorInfo` |
| **150% 缩放下落点仍在工作区内** | `WhenTheWindowIsAt150Percent_ThePlacementStillKeepsItInsideTheWorkArea` | 同上；DPI 靠 `WM_DPICHANGED` 推上去 |
| `Position` / `WorkingArea` 是物理像素 | `AvaloniaWindowPosition_LandsAtExactlyThoseWin32ScreenCoordinates` / `AvaloniaWorkingArea_IsTheMonitorsPhysicalWorkAreaVerbatim` | 同上 |
| `Screen.Scaling` 是倍数不是百分数 | `ScreenScaling_IsAMultiplierOfNinetySixDpi_NotAPercentage` | `GetDpiForWindow` |
| ViewModel 的 `IsVisible` 真的显示/隐藏窗口 | `TheOverlayFollowsTheViewModelsVisibility` | `IsWindowVisible` |
| **跨线程翻转可见性会抛**（两个方向），且抛完两边不一致 | `HidingTheOverlay_*` / `ShowingTheOverlay_*ThrowsToo*` | 异常类型 + `IsWindowVisible` |
| 「不抛」只覆盖「这次调用本来就没事可做」 | `AVisibilityChangeThatChangesNothing_DoesNotThrow_*` | 同上 |
| 跨线程写不翻转可见性的属性不抛（**这是坏消息**） | `WritingABoundProperty_*` / `WritingTheMessage_*` | 控件值读回 |
| 线程检查落在「真改了可见性」而非某个方法 | `TheThreadCheck_FiresOnAnyRealVisibilityChange_NotOnOneParticularMethod` | Show/Hide × 有事/无事 四格 |
| **`screen.Scaling → Compute` 这条接线** | `RepositionOn_TakesTheScalingAndWorkAreaFromTheScreenItIsGiven` | 自造 `Screen(1.5, …)` + `GetWindowRect`，期望值手算 |

---

## A. 只能人看的（观感）

自动化验得出「位置对、穿得透、不抢焦点」，验不出「看起来对」。

| # | 操作 | 期望 |
|---|---|---|
| A1 | 按住热键说话，看右下角 | 绿色条随说话幅度实时起伏；不说话时贴近 0 |
| A2 | 松开热键 | 立刻转成黄色不确定进度动画，不是卡住的绿条 |
| A3 | 制造一次失败（拔掉麦克风再按热键） | 红色 + 短提示文字 |
| A4 | 悬浮条上的文字 | 不溢出、不截断在字中间（`TextTrimming="CharacterEllipsis"`） |
| A5 | 亚克力模糊 | 本机实测 `ActualTransparencyLevel = AcrylicBlur` **已生效**（不是静默回落）；看它是不是真的半透 |

**A6 —— 失败提示会自己消失：** 造一次失败（例如把模型目录指歪再按热键），
红色提示 + 短文字出现后**约 3 秒自行隐藏**（`SessionOptions.FailedDisplayDuration`）。
绕开状态机的那条路（设备变更告知）自己另起计时器，观感须与这条一致。

---

## B. Alt+Tab（D3）：**测得到，但不值得**，故留在这里

自动化只验到「`WS_EX_TOOLWINDOW` 这一位置上了」。**「Windows 真的据此把它排除在 Alt+Tab
之外」验不了**：Windows 没有任何 API 报得出切换器里现在有哪些窗口，任何按 `EnumWindows` +
读 `WS_EX_TOOLWINDOW` 的「预言机」都是拿被测的那一位去证明它自己。唯一独立的判据是
合成 Alt+Tab 按键再截屏认图（做过一次性验证，可重复），那要劫持用户的整个桌面。

**这不是「不可能测到」，是「测得到而不值得」** ——区别要说清楚。

| # | 操作 | 期望 |
|---|---|---|
| B1 | 悬浮条显示时按 `Alt+Tab` | 列表里**没有**它 |
| B2 | 悬浮条显示时看任务栏 | 任务栏上**没有**它 |

---

## C. 多显示器与真高 DPI 屏：本机验不了

**先记机器状态，并记明它会变。** 2026-07-27 实测本机为 **2560×1440 @ 150% 单屏**
（`GetDpiForMonitor` = 144），任务栏在**左侧**，`rcWork = [84,0,2560,1440]`。
**同一天早些时候它还是 1920×1080 @ 100%**（`rcWork = [56,0,1920,1080]`）—— 用户在两次会话之间
改了显示设置，于是当时写进注释的「本机是 100% 单屏」一夜全部过期，连带推翻了一条「某接线零覆盖」
的判断（详见 `RepositionOn_TakesTheScalingAndWorkAreaFromTheScreenItIsGiven` 的注释）。
**凡引用「本机」的结论都要认日期；写测试时别写死屏幕尺寸。**

150% 下已复验、结论**无一改变**：DPI 两腿（且现在不必再合成 `WM_DPICHANGED`，
`Width`=200 DIP 直接量到 300px）、`Screen.Scaling` 读到 1.5（不是 150）、
`Show()` 抹样式、点击穿透六格、`ActualTransparencyLevel = AcrylicBlur`、
`Reposition()` 落点（右缘 2536 = 2560−24、下缘 1416 = 1440−24，边距 16 DIP × 1.5）。

自动化里的 150% 是**给窗口发一条 `WM_DPICHANGED(144)`** 推上去的——那条消息本身走的是
生产路径（把窗口拖到另一块不同缩放的屏上时系统发的就是它），假的只有「显示器没真变」这一件事。
下面这些只有真机器才验得了：

| # | 操作 | 期望 |
|---|---|---|
| C1 | 把系统缩放改成 150% / 200%，重登，看悬浮条 | 仍在主屏工作区右下角，边距看上去仍是 16 个 DIP（物理上 24 / 32 px），不溢出 |
| C2 | 接一块外屏（两块屏不同缩放），把主屏切到外屏 | 悬浮条挪到**新的主屏**右下角——认的是「主屏」，不是「悬浮条当前所在的屏」 |
| C3 | 热插外屏（不改主屏） | 悬浮条位置**重算一次**（`Screens.Changed` → `Reposition`）；不该乱跑 |
| C4 | 副屏在主屏**左侧**（工作区 X 为负） | 落点不被夹回 0；`Compute_WhenClampingOnAScreenThatDoesNotStartAtZero_ClampsToThatScreen` 验的是这条的算术，真机器验的是 Avalonia 报不报得对 |
| C5 | 竖屏 / 缩放非整数倍（125%、175%） | 无溢出、无半像素抖动 |

---

## D. 跑完必须查的脏状态

| # | 查什么 | 命令 | 期望 |
|---|---|---|---|
| D1 | **孤儿窗口**。悬浮条是置顶窗口，测试进程被硬杀时它会留在屏幕上 | 肉眼 + `Get-Process -Name *Lisscribe*` | 屏幕右下角没有残留的黑条；没有残留进程 |
| D2 | **光标位置**。`TheMouseGoesThroughTheOverlay_*` 与 `WithoutTheClickThroughStyles_*` 会把真光标挪到 (560,440) 附近 | 肉眼 | 光标回到了原处（`CursorParked` 负责还原；进程被硬杀则不会还） |
| D3 | **前台窗口**。`ShowingTheOverlay_DoesNotStealTheForegroundWindow` 会建一个普通窗口并抢前台 | 肉眼 | 前台已还给正常的窗口 |

---

## E. 已知无覆盖（别当已验）

1. **跨线程驱动 ViewModel 的两半，只有一半会报。**（本条改过两次：第一版记成「一律不抛」，
   第二版按独立复评记成「只有 `Hide` 抛」，都被实测按回去了。）
   - **真正翻转 `IsVisible` 那一下会抛，Show 与 Hide 两个方向都抛**。分界是「这一次调用有没有
     真的改变可见性」——窗口本来就亮着时那一次 `Show()` 提前返回，才不抛；复评那一格量的正是它。
     这不是「偶尔炸」，是**每次会话的开始与收尾各抛一次**，异常还原样送回引发状态变更的那条线程，
     且抛完之后 ViewModel 与窗口的可见性**不一致**。
     已有三条测试钉住（`HidingTheOverlay_*` / `ShowingTheOverlay_*ThrowsToo*` /
     `TheThreadCheck_FiresOnAnyRealVisibilityChange_*`），外加一条专门留着那个陷阱格
     （`AVisibilityChangeThatChangesNothing_DoesNotThrow_WhichIsHowThisGotMisreadTwice`）。
   - **不翻转可见性的那一半（写 `Volume` / `Message`）不抛，值还真的到了控件上** ——
     它造成的**真实损害**（布局队列被并发改坏、渲染读到半个值）一次也没复现出来，
     连打 500 次也没事。那一半钉住的是「Avalonia 不会告诉你」，**不是**「它安全」。
   - **`SessionController.VolumeChanged`（跑在 WASAPI 采集线程上）只落在后一半**：
     它不动 `IsVisible`，故它撞到的是那个不会报的数据竞争，不是那条会抛的路。
2. **`Show()` 到 `Opened` 之间那几毫秒窗口是不穿透的**（样式刚被 `Show()` 抹掉、还没重设）。
   无解，Avalonia 不给更早的钩子。表现是「悬浮条刚出现的一瞬间点它可能点不到下面」。
3. **`Screens.Primary` 为 null 时保持原位**这条分支没有测试——本机造不出「一块屏都没有」。
4. **`SetLayeredWindowAttributes` 那一步在本机是多余的**（两位齐备而不调它，穿透已经成、
   窗口也照画；独立复评补的第六格）。留着它防的是渲染后端换形态那一天，
   **而那种形态本机换不出来，属推理未实测**。**它失败时不抛**（理由见
   `OverlayWindowStyler` 的注释），故「那一步失败了会怎样」本仓**没有测试杀得掉**。
5. **`TryGetPlatformHandle()` 返回 null 或 0 时这一次不设样式**，只记一条错误日志。
   本机没复现过（构造完就有句柄），故这条分支无测试。
6. **`OnOpened` / `OnScreensChanged` 的 catch 边界只测了顺境。** 造不出「Win32 设样式瞬时失败」，
   故「异常真被咽住、没抛回状态机的线程」这件事**本仓没有测试杀得掉**——
   只有代码结构与注释在担保。
7. **`Close()` 之后再 `Show()` 会怎样：未实测，推理。** 已因此**刻意不退订 `Opened`**
   （退掉的话窗口会以全无点击穿透样式之态回屏，且不再随显示器变化重排）。
8. 真锁屏 / 真休眠 / UAC secure desktop 下悬浮条的行为——见
   `PlatformIntegrationManualChecklist.md` 的 A / C 组，同样未执行。
