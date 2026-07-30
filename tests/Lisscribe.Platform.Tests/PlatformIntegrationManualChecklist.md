# 平台原生集成手工验收清单（窗口探测 / 会话监听 / 悬浮窗样式 / 自启动）

**先说清楚这份清单**不**包含什么。** 下列各项**已经自动化**，不在本清单里重复
（做法是自建真窗口，代价不过一个 `TestWindow.cs`；**别用 `Process.GetCurrentProcess().MainWindowHandle`
取句柄**——测试宿主是控制台进程，那个属性恒为 0，测试会每次 Skip 而看着是绿的）：

| 已自动化的行为 | 测试 |
|---|---|
| 同进程的真窗口必可注入 | `CanInjectInto_ForAWindowOfOurOwnProcess_IsTrue` |
| 无效 / 已销毁的句柄判不可注入且不抛 | `CanInjectInto_ForAnInvalidHandle_*` / `..._ForAWindowThatHasBeenDestroyed_IsFalse` |
| 前台窗口取的是**真的**在前面那个 | `GetForegroundWindow_ReturnsTheWindowThatIsActuallyInFront` |
| 完整性级别 SID 解析正确 | `OurOwnIntegrityLevel_MatchesWhetherWeAreElevated` |
| **UIPI 判据的方向**（目标更高不可注入 / 更低可注入） | `CanInjectInto_WhenTheTargetSitsAboveUs_IsFalse` / `..._SitsBelowUs_IsTrue` |
| 泵没停干净时故意保留 GC root | `WhenThePumpCannotBeStopped_TheDelegateRootIsDeliberatelyLeaked` |
| 交接失败时默认落在「没停干净」那一侧 | `WhenTheHandoverFails_TheRootIsKeptRatherThanAssumedClean` |
| 收尾中途出错也把该释放的释放完 | `WhenTheTeardownThrowsHalfway_ItStillFinishesReleasingWhatItPromised` |
| 记日志失败不吞掉锁屏 / 休眠事件 | `WhenLoggingThe{Lock,Suspend}ItselfThrows_TheEventStillReachesSubscribers` |
| 装不上 WinEvent 钩子是降级不是启动失败 | `Start_SurvivesASinkThatThrowsOnEveryEntry`（第四轮才真正钉住：此前测试缝短路在生产分支之前，把那条决定反转成 `throw` 时 169 条测试一条不红） |
| 四处 `.Safe()` 包裹都还在 | `WhenLoggingItsOwnStartupThrows_StartStillSucceeds`（KeyboardHook）/ `TheLoggerIsWrappedSoThatLoggingCannotFaultTheCallingWorker`（WindowProbe）/ 上面两类（SessionMonitor、MessagePumpThread） |
| 日志被咽掉时留下可诊断的痕迹 | `ASwallowedEntry_LeavesATraceForDiagnosticsToFind` |
| 记日志失败不弄死消息泵线程、不让 Post 改抛 | `WhenLoggingItselfThrows_ThePumpThreadStillSurvives` / `PostToADeadPump_...` |
| 日志装饰器三个成员都不外泄异常 | `SafeLoggerTests` |
| 点击穿透三个位真的置上了 | `MakeClickThrough_SetsAllThreeExtendedStyleBits` |
| 原有扩展样式没被抹掉 | `MakeClickThrough_KeepsTheExtendedStylesTheWindowAlreadyHad` |
| 设样式失败必抛，不静默 | `MakeClickThrough_WhenTheSystemRefuses_Throws` |
| **锁屏**引发 `SessionLocked` | `SessionLocked_FiresWhenTheSessionIsLocked` |
| **解锁不**引发 `SessionLocked` | `SessionLocked_DoesNotFireWhenTheSessionIsUnlocked` |
| **休眠**引发 `SystemSuspending` | `SystemSuspending_FiresWhenTheSystemIsAboutToSuspend` |
| 前台窗口变化引发 `ForegroundWindowChanged` | `ForegroundWindowChanged_ArrivesEvenThoughStartRanOnAThreadWithNoMessagePump` |
| 回调里订阅者抛异常不崩、不断流 | `ASubscriberThatThrowsOn*` |
| Dispose 之后不再有任何事件（含 SystemEvents 退订） | `AfterDispose_NoMoreEvents` |
| 一个进程内可并存两个监听器 | `TwoMonitorsInTheSameProcess_BothGetTheirEvents` |
| 自启动值整串带引号、不重复加引号、类型为 REG_SZ | `Enable_*` |

锁屏与休眠之所以能自动化，是因为往**本进程自己的**顶级窗口投 `WM_WTSSESSION_CHANGE` /
`WM_POWERBROADCAST` 就能驱动 `SystemEvents`（它的隐藏窗口正在其中，实测类名
`.NET-BroadcastEventWindow.5c39d4.0`）。**但那证明的是「消息到了我们做得对」，
不是「Windows 真会发这条消息」** —— 后者仍在下面。

---

## A. 真锁屏 / 真休眠（自动化只走了一半）

**判据**：控制台打出 `SessionLocked` / `SystemSuspending`，且**解锁后不再打第二条**。

| # | 操作 | 期望 |
|---|---|---|
| A1 | 按 `Win+L` 锁屏，等 5 秒，解锁 | 锁屏瞬间恰好一条 `SessionLocked`；解锁不再有 |
| A2 | 按住热键说话中途按 `Win+L` | `SessionLocked` 到达 → 会话被取消、录音丢弃；解锁后**下一次热键立刻可用**（这一条是关键：`ForgetHeldMainKey` 若没被接上，下一次热键会被静默吞掉，要按到第三次才恢复） |
| A3 | 合上盖子 / `shutdown /h` 休眠，唤醒 | 睡下去之前恰好一条 `SystemSuspending`；唤醒不再有 |
| A4 | 切换用户（不注销） | `SessionLocked`（`ConsoleDisconnect`） |

**A1/A3 本轮没做**：锁屏会把开发机锁在门外、休眠会中断整个工作流，二者都超出「跑一次测试」的
代价范围。它们是真正只能由人挑时机做的。

---

## B. UIPI：向更高完整性级别的窗口注入 —— **每次改动 `WindowProbe` 必跑**

比较**方向**现在自动化了（`CanInjectInto_WhenTheTargetSitsAboveUs_IsFalse` /
`..._SitsBelowUs_IsTrue`：目标那侧是真窗口真 SID 解析，只把「我们自己是什么级别」注进去）。
本组要验的是那件自动化替不了的事：**真实的 UIPI 行为与我们的判据一致**——
即 Windows 真的会丢掉发往高完整性窗口的 SendInput，而不只是我们以为它会。

本组**已执行过一轮**：B1 提权 PowerShell 窗口 → `false` ✅；B2 普通 Notepad（IL = 0x2000）→ `true` ✅。

| # | 操作 | 期望 |
|---|---|---|
| B1 | 以管理员身份开一个 PowerShell，让它成为前台窗口，读 `CanInjectInto(GetForegroundWindow())` | **false** ——**已执行 ✅** |
| B2 | 阳性对照：同一轮里换成普通记事本（IL = 0x2000） | **true** ——**已执行 ✅**（没有这条，B1 的 false 也可能只是因为函数恒返回 false） |
| B3 | 焦点在任务管理器上 | **false** ——**未执行**（B1 已覆盖同一条判据，B3 只是换一个提权程序，优先级低） |
| B4 | 跑完 B1–B3 **立刻回 E0**：提权窗口留在前台会让整组注入型测试报出指不到真因的失败 | 前台已还给普通窗口 |

---

## C. UAC secure desktop：**本机测不了，原因已查清**

问题是：UAC 提权框切 secure desktop 时，`SessionSwitch` / `PowerModeChanged` /
前台窗口变化各自到不到？它关系到一条真实的失败链——用户按住热键说话时 UAC 弹框，主键的 KEYUP
落在 secure desktop 上我们收不到，若此刻也没有会话事件，`ForgetHeldMainKey()` 就无人触发。

**本机实测结论：这台机器上根本弹不出 secure desktop，故这个问题在此测不出来。**
`HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System` 实测：

    EnableLUA                  = 1      （UAC 开着，令牌确实被过滤：进程实测为 Medium）
    ConsentPromptBehaviorAdmin = 0      （管理员**静默提权，根本不弹框**）
    PromptOnSecureDesktop      = 0      （就算弹框，也**不切 secure desktop**）

**把两类证据分开写**（上一版这里的推理是循环的，据评审改正）：

- **实测**：本机 `ConsentPromptBehaviorAdmin = 0`、`PromptOnSecureDesktop = 0`；
  两次 `-Verb RunAs` **零弹框**。
- **文档依据、本机无从实测**：「0 并非出厂默认」（微软文档记出厂默认为 5 / 1）。

零弹框只证明**本机当前行为**；要由它推出「本机非默认」，得先接受「默认配置会弹框」——
而那正是那条无从查证的文档依据。故不再声称「已直接证明」。
所幸对本任务的决定没有影响：**不把提权做进自动化测试**，靠的是「本机零弹框这件事本身就说明
这条路的行为随机器策略而变」，那一条只需要实测部分。

配套实测：装上 `EVENT_SYSTEM_FOREGROUND..EVENT_SYSTEM_DESKTOPSWITCH` 全段的 WinEvent 钩子 +
`SessionSwitch` / `PowerModeChanged` / `SessionEnding` 三个处理器，然后
`Process.Start(cmd.exe, Verb = "runas")`：提权进程 **0.13 秒**就起来了（没有任何提示框），
其后 45 秒内收到的全部事件是 **2 条 `EVENT_SYSTEM_FOREGROUND`**，
**0 条 `DESKTOPSWITCH`、0 条 `SessionSwitch`、0 条 `PowerModeChanged`**。
即：**静默提权只产生前台窗口变化**，与推理一致，但它**不是** secure desktop 的现场。

**要真测这一条，须先改机器范围的安全策略**（`ConsentPromptBehaviorAdmin = 2`、
`PromptOnSecureDesktop = 1`，改完注销或重启），测完再改回。这是用户的机器策略，
本任务**不擅自改**。

| # | 操作 | 期望 / 要记的事实 |
|---|---|---|
| C0 | **先记下原值**（C5 要按原值回写，别照抄本文里的数字——那是本机当前值，不是 Windows 出厂默认；据微软文档出厂默认为 `ConsentPromptBehaviorAdmin = 5` / `PromptOnSecureDesktop = 1`，**这两个数字是文档依据、本机无从实测**，本机实测值为 0 / 0）：<br>`Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' \| Select-Object EnableLUA, ConsentPromptBehaviorAdmin, PromptOnSecureDesktop` | 把三个数字抄下来 |
| C1 | 临时打开 secure desktop 提示（需管理员），然后**注销或重启**使其生效：<br>`Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name ConsentPromptBehaviorAdmin -Value 2`<br>`Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -Name PromptOnSecureDesktop -Value 1` | 提权时屏幕变暗并弹框 |
| C2 | 按住热键说话，中途触发一次提权（例如 `Start-Process cmd -Verb RunAs`） | 记下 `SessionSwitch` / `PowerModeChanged` / `FOREGROUND` / `DESKTOPSWITCH` **各自到没到** |
| C3 | 松开热键（此时人已在 secure desktop 上，KEYUP 收不到） | 记下会话是靠什么收的尾：会话事件？还是只剩 `MaxRecordingSeconds = 60` 兜底？ |
| C4 | 提权框消失后立刻再按一次热键 | **必须立刻响应**。要按两三次才响应，即说明主键按住态没人复位 |
| C5 | **回写 C0 记下的那两个原值**，再注销/重启 | 与 C0 抄下的数字一致。**别回写本文里的 0/0**——那只是本机当时的状态，把别人的机器改成非默认才是真的坏事 |

**若 C2 证实会话事件一条也不到**，现成的补法是把 `EVENT_SYSTEM_DESKTOPSWITCH` 也纳入
`SessionMonitor`（钩子范围改成 `EVENT_SYSTEM_FOREGROUND..EVENT_SYSTEM_DESKTOPSWITCH`，
按 `eventType` 分派，桌面切换走 `SessionLocked`）。**本任务不预先实现它**——
没有证据表明需要，而多发一次 `SessionLocked` 就是白白取消用户一次录音。

---

## D. 悬浮窗真实观感

详见 `tests/Lisscribe.App.Tests/OverlayIndicatorManualChecklist.md`，操作步骤也在那份里。

D1（点击穿透）与 D2（不抢焦点）**已做成自动化**，各带阳性对照；**并且 D1 当场抓出一个真缺陷**：
穿透要的是 `WS_EX_LAYERED` + `WS_EX_TRANSPARENT` 两位，少了前者时补多少次
`SetWindowPos(SWP_FRAMECHANGED)` 都没用（四格实测表见 `OverlayWindowStyler` 的类注释）；
且**不能在 `Show()` 之前置样式**，`Show()` 会把扩展样式整个抹掉。

D3（Alt+Tab）与 D4（多显示器 / 高 DPI）仍是手工。

| # | 操作 | 期望 |
|---|---|---|
| D1 | 悬浮条盖住托盘图标，点它 | 点击**穿透**到托盘图标 —— **已自动化 ✅**（`TheMouseGoesThroughTheOverlay_ToWhateverIsUnderneath`）。托盘图标那一处仍宜人验一次 |
| D2 | 悬浮条出现时，焦点是否被抢走 | 焦点窗口**不变** —— 「显示时不抢前台」这个**行为**已自动化 ✅（`ShowingTheOverlay_DoesNotStealTheForegroundWindow`，带「本进程抢得动前台」的阳性对照）；但 **`WS_EX_NOACTIVATE` 这一位本身仍只验到「位上了」**：变异实测删掉该位，那条行为测试**照绿**（推理：`ShowActivated=false` 已令 Avalonia 走 `SW_SHOWNOACTIVATE`，独力满足断言）。与 D3 同弱 |
| D3 | 悬浮条显示时按 `Alt+Tab` | 列表里**没有**它 —— **仍手工**：Windows 没有 API 报得出切换器内容，唯一独立判据是合成按键 + 截屏认图，那要劫持整个桌面。「测得到而不值得」，不是「不可能测到」 |
| D4 | 多显示器 / 不同 DPI 缩放下重复 D1–D3 | **仍手工**：本机是单屏（2026-07-27 实测 2560×1440 @150%；同日更早为 1920×1080 @100%，两种缩放下 D1 六格与 D2 结论一致）。多显示器那一半仍无从验 |

---

## E0. 跑**之前**要查的一件事（否则会白查半天）

**当前前台窗口的进程，完整性级别不能高于测试宿主。**

前台一旦落在 SYSTEM 完整性级别的窗口上（做完提权实验后落在 `GameInputServiceWindow` 是一例），
**未改一行代码**也会有几十条测试转红、耗时涨几倍，
而失败讯息是「只收到 []」「系统拒绝了前台抢占」——**一个字都指不到真因**。

已做成 fail-fast 自检（`ForegroundSanityCheck`，挂在 `InjectedInputCollection` 的 fixture 上）：
命中时整组注入型测试立刻中止，并打印真因与恢复办法，不再让几十条测试各自超时。

恢复办法（把前台还给一个普通窗口）：

```powershell
(New-Object -ComObject Shell.Application).MinimizeAll()
(New-Object -ComObject Shell.Application).UndoMinimizeALL()
```

---

## E. 跑完必须查的脏状态

**每次跑完 `Lisscribe.Platform.Tests` 都要过一遍。**

| # | 查什么 | 命令 | 期望 |
|---|---|---|---|
| E1 | **注册表 Run 项残留**。`RegistryAutoStartTests` 写的是**真实的 HKCU Run 项**，值名 `LisscribeTest_<GUID>`，`Dispose` 里删。但测试进程被硬杀时 `Dispose` 不跑。 | `Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'` | 没有任何 `LisscribeTest_` 开头的值 |
| E1' | 清法（**别用 `Get-Item` 的 `DeleteValue`**：那样打开的键是只读的，实测抛 `Cannot write to the registry key`） | `$p='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'; (Get-Item $p).GetValueNames() \| Where-Object { $_ -like 'LisscribeTest_*' } \| ForEach-Object { Remove-ItemProperty -Path $p -Name $_ -Force }` | 再查 E1 为空 |
| E2 | **孤儿窗口**。`TestWindow` 建的是真顶级窗口，线程没退干净就会留下。 | `Get-Process -Name *testhost*, *Lisscribe*` | 没有残留进程 |
| E3 | **卡住的修饰键**（键盘测试的老账，本类同一个 Collection 内跑） | 随便打几个字 | 没有 Ctrl/Alt/Shift 粘住 |
| E4 | 屏幕上有没有留下测试窗口 | 肉眼 | 没有 |

**另记一条本类特有的**：`SessionMonitorTests` 会往本进程自己的窗口投「会话已锁定 / 系统即将休眠」
消息。它**只投给本进程**（按 PID 过滤），不会影响桌面上别的程序——但也因此，
测试宿主进程内**所有**活着的 `SessionMonitor` 都会收到，故这些测试与注入型测试同属
`InjectedInputCollection`（禁并行）。

---

## F. 本轮已执行 / 未执行

| 组 | 状态 |
|---|---|
| A（真锁屏 / 真休眠） | **未执行**——会把开发机锁住或中断工作流 |
| B（UIPI 提权窗口） | **B1 / B2 已执行 ✅**（提权窗口 false、普通 Notepad true）；**B3 未执行**；**B4 每次跑完 B 组必做** |
| C（UAC secure desktop） | **已查明本机测不了**，原因与实测数据见上 |
| D（悬浮窗观感） | D1 / D2 做成了自动化并抓出真缺陷（缺 `WS_EX_LAYERED`、`Show()` 会抹样式）；D3 / D4 仍手工。**「D3 也能自动化」这个说法要打折看**——那次是人眼比对两张 Alt+Tab 切换器截图，可重复，但要劫持整个桌面且判据是认图，不适合进常跑套件 |
| E0（前台完整性级别） | **已做成 fail-fast 自检**，见上 |
| E（脏状态） | **已执行** |
