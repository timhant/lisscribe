# KeyboardHook 手工验收清单

**先说清楚这份清单**不**包含什么。** 低层键盘钩子看着「无法自动化（需要真实按键与真实前台窗口）」，
其实不然：用 `SendInput` 合成的按键会走完整条 `WH_KEYBOARD_LL` 链，钩子分不出它与真实按键，
故下列各项**已经做成自动化测试**，不在本清单里重复：

| 已自动化的行为 | 测试 |
|---|---|
| 组合成立时先 Pressed 后 Released | `PressingTheCombination_RaisesPressedThenReleased` |
| 事件次序不因订阅者慢而颠倒 | `Events_KeepTheirOrder_EvenWhenTheFirstHandlerIsSlow` |
| 订阅者抛异常不崩进程、不吞掉后续事件 | `ASubscriberThatThrows_NeitherKillsTheProcessNorStopsLaterEvents` |
| 主键被吞，链上后续钩子看不到它 | `TheMainKey_IsSwallowed_SoTheRestOfTheHookChainNeverSeesIt` |
| 修饰键被放行 | `ModifierKeys_ArePassedThroughEvenWhileTheHotkeyMatches` |
| 带我们自己注入记号的按键被放行且不触发事件 | `KeysCarryingOurOwnInjectionTag_AreIgnoredAndPassedThrough` |
| 按住不放只触发一次 Pressed | `HoldingTheMainKeyDown_RaisesPressedOnlyOnce` |
| Released 携带的时长可信 | `Released_CarriesRoughlyHowLongTheMainKeyWasHeld` |
| Stop 之后不再有事件 | `AfterStop_SynthesizedKeysRaiseNoEvents` |
| 存活自检重装之后钩子仍然活着 | `TheHealthCheck_ReinstallsTheHookWithoutKillingIt` |
| 按住期间 `IsMainKeyPhysicallyDown` 为真 | `IsMainKeyPhysicallyDown_IsTrueWhileTheSwallowedMainKeyIsHeld` |
| 一个进程内可并存两个钩子 | `TwoHooksInTheSameProcess_BothWork` |
| 重同步修饰键能认出钩子起来前就按下的修饰键 | `ResyncModifiers_PicksUpAModifierThatWasAlreadyDownBeforeTheHookStarted` |
| 重同步修饰键**不会**截断按住中的会话 | `ResyncModifiers_WhileTheHotkeyIsHeld_DoesNotCutTheSessionShort` |
| 连续三次重装失败必报 `HookLost` | `ThreeConsecutiveReinstallFailures_ReportHookLost` |
| 重启钩子不带过上一轮的自检状态 | `RestartingTheHook_DoesNotCarryOver*` |

下面留下的，是**真的只能由人来验**的：需要真实前台窗口、需要另一个安全级别的进程、需要第三方
输入法、需要真实的时间流逝或真实的桌面切换。

**跑法**：把程序整个跑起来；或用一个最小控制台程序装配 `KeyboardHook`，订阅三个事件
并打印时间戳。热键用配置默认值。

**每条都写了可证伪的判据。** 「工作正常」不是判据。

---

## A. 需要真实前台窗口

**A 组已执行，2026-07-26，Win11 22631 / .NET 10。** 用的是生产的 `KeyboardHook`，热键
`Ctrl+Alt+Space`。目标窗口不是记事本，而是一个自建的顶层窗口（带**真菜单栏** + 一个 EDIT
子控件）：判据因此从「看上去对不对」变成消息级事实，且不碰用户正在用的任何程序。

- [x] **A1 主键真的进不去目标应用。** 窗口取得前台焦点，按住热键 1 秒后松开。
      判据：EDIT 控件内容长度为 0，且打印了 Pressed / Released。
      **实测**：事件 `[Pressed, Released(1013ms)]`，EDIT 内容 `""`（长度 0）。**通过。**
      *（自动化测试只能验「链上后续钩子看不见它」，验不了「目标窗口的编辑区没变化」——
      本条补的正是「返回 1 → 系统不投递给目标窗口」这最后一跳。）*

- [x] **A2 单按主键仍照常上屏。** 单独按一下 Space（不按修饰键）。
      判据：EDIT 里出现一个空格，且无任何事件。
      **实测**：事件 `[]`，EDIT 内容 `" "`（长度 1）。**通过。**

- [x] **A3 长按 Alt 不激活目标应用菜单栏。** 按住 `Ctrl+Alt+Space` 3 秒再松开，
      窗口过程记录 `WM_SYSCOMMAND(SC_KEYMENU)` / `WM_ENTERMENULOOP` / `WM_INITMENU`。
      判据：热键期间一条菜单消息也不收到；**且阳性对照（真按一下 Alt）必须收到**，
      否则只能说明这个窗口压根收不到菜单消息，本条不作数。
      **实测**：热键期间 `[]`；阳性对照
      `[WM_SYSCOMMAND(SC_KEYMENU), WM_ENTERMENULOOP, WM_INITMENU]`。**通过。**
      *（另一半「补发的 Alt KEYUP 会不会激活菜单栏」也实测过，同样不会。
      菜单栏的键盘导航要靠一次“干净的 Alt 按下-松开”触发，中间夹了别的键就不算——
      **但 Alt 是唯一修饰键时不适用**，那一格见 `AltOnlyHotkeyMenuTests`。）*

## B. 需要另一个安全级别的进程 / 另一个桌面

- [ ] **B1 UIPI：焦点在提权进程上时收不到。**
      **先做阳性对照**：焦点在普通窗口上按一次热键，确认打印了 Pressed。跳过这一步的话，
      「没有 Pressed」同样可以是「程序压根没跑起来」——否定判据没有对照就什么也没验。
      然后以管理员身份运行 PowerShell，令其获得焦点，按热键。
      判据：对照那次**有** Pressed，提权窗口那次**没有**。这是已知限制，不是 bug——记录现象即可。

- [ ] **B2 secure desktop 期间的 KEYUP 丢失。** 按住热键不放，令 UAC 提权框弹出（比如另开一个
      需要提权的程序），在提权框上松开主键，然后取消提权框。
      判据：录音**不会**在松开时结束（预期如此，见下），而是在 `MaxRecordingSeconds` 到点时
      自动收尾并给出「已达最长录音时长」。
      *（这条是本任务缩掉的那张安全网的现场。看门狗的「KEYUP 丢失」分支对被吞的热键已无救援
      能力——理由与实测数据写在 `KeyboardHook.IsMainKeyPhysicallyDown` 的注释里。本条要确认的
      是「兜底真的兜住了」，不是「看门狗还灵」。须把整个程序跑起来才验得了。）*

- [ ] **B3 锁屏解锁后仍工作。** 按 `Win+L` 锁屏，解锁，再按热键。
      判据：Pressed / Released 照常打印，且**修饰键状态表没有残留**——解锁后单按一次主键
      应当照常上屏（若残留，会被误判成组合成立而吞掉）。

## C. 需要第三方输入法

- [ ] **C1 微软拼音不受影响。** 微软拼音处于中文态，在记事本里按 `Ctrl+空格`。
      判据：中英文照常切换。
      *（只核过微软拼音；搜狗 / 讯飞 / RIME 未核。）*

- [ ] **C2 第三方输入法。** 装一个非微软输入法（搜狗或 RIME），重复 C1 与 A1。
      判据：同上，且热键仍能触发。

## D. 需要真实的时间流逝

- [ ] **D1 委托没被 GC 回收（自然时长那一半）。** 程序保持运行 10 分钟，其间不按热键；
      随后按一次热键。
      判据：Pressed / Released 照常打印，且**没有 AccessViolation**。
      若此处崩溃，检查 `KeyboardHook.RootedCallbacks` 是否确为静态字段——
      委托被回收后按键即 `AccessViolation`，而 `[LibraryImport]` 生成的 `GC.KeepAlive` 挡不住它。

      *（强制 GC 那一半已自动化：`TheHookKeepsWorkingAcrossAggressiveGarbageCollection`。
      但**它杀不掉「把 RootedCallbacks 改成实例字段」那个变异**，原因写在该测试的注释里：
      `MessagePumpThread` 的 `_onStart` / `_onStop` 捕获了 `this`，泵线程活着就连带 root 住了
      整个 `KeyboardHook` 实例。也就是说静态 root 眼下是双保险中的一道，而另一道只在泵线程
      活着时成立——恰恰不覆盖它要防的窗口。故本条仍留在手工清单里。）*

- [ ] **D2 存活自检长时间无副作用。** 程序运行 30 分钟（默认自检间隔 30 秒，即约 60 次重装），
      其间每 5 分钟按一次热键。
      判据：每次都正常响应，且日志里**没有**「检出钩子曾被系统摘除」。
      *（自动化测试把间隔压到 100ms 跑了 2 次；这条验的是长期累积——句柄泄漏、链上位置漂移。）*

- [ ] **D3 回调超时后的自愈。**

      **先读这一条再动手：这里其实有两个不同的阈值，别混成一条。**

      - **a「系统愿意等钩子线程多久」**：实测封顶约 1000ms 量级（随机器与负载浮动）。
        超了系统**丢掉这一次按键、但不摘钩子**。见下面 E2。
      - **b「回调本身跑太久就摘钩子」**：这一条才由 `LowLevelHooksTimeout` 管。
        **本机实测该值为 25000（25 秒），不是默认的 300ms**
        （`HKCU:\Control Panel\Desktop`，本机既有设置，非本项目所留）。
        实测让回调连续 6 次各跑 2 秒，系统一次也没摘钩子。

      本条验的是 **b**。故**不临时把该值调小，这一条就永远触发不了**，跑了也等于没跑。
      （a 那条阈值不受它影响，本机照样能复现丢键。）

      做法：把 `LowLevelHooksTimeout` 临时改成 100（注销重登才生效），再在 `HandleCallback`
      里临时 `Thread.Sleep(2000)`，然后按几次热键。
      判据：热键先失灵；随后在一个自检周期（默认 5 秒）内恢复；日志先出现
      「钩子回调耗时 …ms，已达重装阈值」，再出现「检出钩子曾被系统摘除（回调超时所致），
      已重新安装」。**验完务必把注册表值和临时 Sleep 都撤掉。**

- [ ] **D4 丢一次 KEYUP 之后不会连累下一次。** 承接 D3 或 B2：在钩子被摘除 / 切 secure desktop
      导致主键 KEYUP 丢失之后，连按两次热键。
      判据：**第一次就有 Pressed**。若要按到第二次才有反应，说明「主键还按着」的标志没被复位
      （`ForgetHeldMainKey` 没生效），键被 `HotkeyMatcher` 当成重复 KEYDOWN 静默吞掉了。
      *（复位路径已自动化：`AfterALostKeyUp_TheNextHotkeyPressStillWorks` 与
      `ForgetHeldMainKey_DoesResetTheHeldState`——注意复位出口是 **`ForgetHeldMainKey()`**，
      不是 `ResyncModifiers()`，后者只重同步修饰键、刻意不碰主键按住态。
      本条验的是「钩子被摘除」那条路径，它依赖 D3 才能造出现场。）*

## E. 环境相关

- [ ] **E1 全屏独占游戏 / 远程桌面下的行为。** 记录现象即可，本阶段不设判据。

- [ ] **E2 已知限制：泵线程被拖住约 1 秒即丢一次热键，且程序端零证据。**
      这不是待修的缺陷，是要确认「现象如实、且我们确实无从感知」。
      造法：临时往泵线程 `Post` 一个睡 1.3 秒的动作，其间按一次热键。
      判据：那一次热键**没有** Pressed；随后钩子句柄没变、`CompletedHealthChecks` 没涨、
      下一次热键一切正常。实测数据（单次采样，随机器与负载浮动）：

      ```
      停滞  900ms → 系统等了  805ms，热键照常触发
      停滞 1100ms → 系统等了  988ms，热键照常触发
      停滞 1300ms → 系统等了 1196ms，键被丢
      停滞 1500ms → 系统等了 1402ms，键被丢
      ```

      边界在约 1000ms 量级（与注册表 `LowLevelHooksTimeout` 在 1709+ 的 1000ms 封顶相符）。
      **超时后系统丢的是这一次按键，并不摘钩子**，故存活自检既救不了也发现不了。
      **谁往泵线程上加活，谁就在制造这类丢键**——而存活自检本身就跑在这条线程上。

---

## 跑完必做

- [ ] 确认没有键卡在按住态。实测低层钩子吞掉 KEYUP 时 win32k 不更新异步键状态表，该键会
      **卡在系统级「按住」态且跨进程存活**。验完随手在记事本里敲几个字、按几下 Ctrl，
      确认输入正常。

      正常跑测试时这件事由 `SynthesizedInput.ReleaseEverythingUsedByTests()` 自行收尾，
      **但跑变异时尤其要查**：变异若让进程被硬杀（例如把事件派发改回
      `ThreadPool.UnsafeQueueUserWorkItem`，订阅者一抛异常进程当场终结），
      测试类的 `Dispose` 根本不会跑，实测就留下了 `LCtrl (0xA2)` 卡在按住态。
      查法（PowerShell）：

      ```powershell
      Add-Type -Name K -Namespace W -MemberDefinition '[DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);'
      0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,0x20,0x7C,0x7D,0x7E,0x7F |
        Where-Object { ([W.K]::GetAsyncKeyState($_) -band 0x8000) -ne 0 } |
        ForEach-Object { "卡住: 0x{0:X2}" -f $_ }
      ```

      清法：对卡住的键补发一次**被放行**的 KEYUP（`SendInput` 带 `KEYEVENTF_KEYUP`）。
