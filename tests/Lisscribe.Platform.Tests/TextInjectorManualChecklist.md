# 文本注入手工验收清单

**先说清楚这份清单为什么这么短。**「`SendInput` 与剪贴板无法自动化验证」是个错判：
自建一个带 EDIT 控件的窗口 → 抢前台 → 注入 → 机械读回控件内容，整条链都是可编程的。
于是原本打算手工走的十来项里，**十项已经变成常规测试**（`SendInputTextInjectorTests` 7 条、
`ClipboardTextInjectorTests` 11 条、`CompositeTextInjectorTests` 6 条），跟着每次
`dotnet test` 跑，不再依赖谁记得手动验一遍。已自动化的包括：

- 注入「打开设置」→ 逐字比对（`InjectAsync_PutsTheExactTextIntoTheFocusedControl`）
- 注入 `😀` 与扩展 B 区汉字 `𠀋` → 代理对完好（`InjectAsync_KeepsSurrogatePairsIntact`）
- 按住 Ctrl+Alt 时注入 → 修饰键被释放（`InjectAsync_ReleasesTheModifiersTheUserIsStillHolding`，
  两条路径各一条，并各配了负对照）
- UIPI 判定为不可注入时**一个字符也不发**（`InjectAsync_WhenTheTargetSitsAboveUs_SendsNotOneCharacter`）
- 焦点已切走时**一个字符也不发**（`InjectAsync_WhenTheFocusHasMovedElsewhere_SendsNotOneCharacter`）
- `targetWindow = 0` 时只复制不粘贴（`InjectAsync_WithZeroTarget_CopiesWithoutPasting`）
- `restoreClipboard` 开/关两侧的恢复行为
- 另一个进程占着剪贴板时报 Failed 而非 Ok（`InjectAsync_WhenAnotherProcessHoldsTheClipboard_...`，
  真起子进程 + 真 HWND 持有）
- 选路器的全部分支

下面只剩**真的只能人验**的四项：靶子是别人的代码，或者现场会弹 UAC 把测试挂住。

跑法：用最小控制台程序，3 秒倒计时后向当前前台窗口注入指定文本。

## A 组：第三方应用（靶子是别人的代码，自建窗口代表不了它们）

- [ ] **VS Code**：按住 `Ctrl+Alt` 不放的同时触发注入 → 文本正常进入，**不触发任何快捷键**
      （不会折叠代码、切分屏、跳定义）。
      **这一条必须人验，理由是实测结论**：纯 user32 EDIT 控件在按住 Ctrl+Alt 时**照样**收全
      `KEYEVENTF_UNICODE` 注入的每一个字符（Win11 22631 实测），所以自动化测试里
      「文本进去了」证明不了不变量 1 —— 自动化那侧改用「注入后修饰键是否已释放」当预言机。
      而 VS Code 是 Chromium，自己处理加速键，**推测**它才是这条不变量真正的受益者；
      这一层至今仍是推理，本项就是去证实或证伪它的。
- [ ] **微信**输入框注入 → 正常。（同类工具多把微信单列成「只能走剪贴板」的应用，重点验。）
- [ ] **微软拼音开启状态**下注入 → 文本不被输入法二次转换、不进候选窗。
- [ ] 第三方输入法（搜狗/讯飞任一）下重复上一项 —— 至今只核过微软拼音。
- [ ] **按住 `Win` + 开着输入法时注入** —— 验那个中间键 `VK_NEUTRAL = 0xE8` 的副作用。
      已实测的只有本机 US 布局（0x04090409）下的纯 EDIT 靶子：不留字符，
      且 `MapVirtualKeyW(0xE8, VK_TO_VSC) = 0`、`MapVirtualKeyExW(0xE8, VK_TO_CHAR, 0x04090409) = 0`
      （对照 `'V'`→86、`F13`→scan 100），即这个码在本布局下既没有扫描码也不产字符。
      **未实测三项**：换键盘布局、第三方输入法、按扫描码配对 make/break 的 RawInput 消费者
      （游戏、DirectInput、转发扫描码的 RDP/VNC）。判据：不多出任何字符、不打断候选窗。

## B 组：真提权窗口（自动化会弹 UAC 把整轮测试挂住）

- [ ] 焦点在「以管理员身份运行的 PowerShell」→ 返回 `IntegrityLevelTooLow`，**不静默丢字**。
- [ ] 焦点在任务管理器 → 同上。
- [ ] 同上两项、配置 `output.mode = "Clipboard"` → 同样返回 `IntegrityLevelTooLow`，
      且**用户原本的剪贴板内容未被顶掉**（守卫在写剪贴板之前）。

> 说明：`CanInjectInto` 的**判定逻辑**已由 `WindowProbeTests` 用注入自身完整性级别的方式钉住，
> 本组验的是「真实 UIPI 行为与那个判定一致」。

## C 组：真实剪贴板并发与时序

- [ ] 一边在浏览器里反复 `Ctrl+C` 一边注入 → 不崩溃，且失败时报 Failed 而不是 Ok。
      （`OpenClipboard` 的重试已由子进程测试覆盖；本项验的是真实应用的持有节奏。）
- [ ] **关掉 Windows 剪贴板历史（设置 → 系统 → 剪贴板）后重跑上一项**，观察静默失败率是否下降。
      背景：实测约 **1%** 的 Ctrl+V 会「按键到了、目标控件也发了 WM_PASTE，可一个字符都没进去」，
      现场是剪贴板内容完好、焦点前台修饰键全对，隔 200ms 补发一次立刻成功——即目标控件
      在 WM_PASTE 里自己调的 OpenClipboard 那一刻失败了。**这一层察觉不到，故会返回 Ok。**
      这是「剪贴板 + Ctrl+V」的固有代价（见 `ClipboardTextInjector` 已知限制二），
      也是默认 `output.mode = "SendInput"` 的一条实打实的理由。本项只是确认成因，不是待修缺陷。
- [ ] **`PasteSettleDelayMs = 150` 到底该是多少** —— 这个值**没有实测支撑**：把它改成 0，
      自建 EDIT 靶子上 5 连跑全绿（EDIT 在 WM_PASTE 里同步读完剪贴板才返回，这类靶子测不到）。
      真正会中招的是**异步取剪贴板**的应用。做法：在微信 / VS Code / 浏览器里，
      开 `restoreClipboard = true`，把该值依次调成 0 / 30 / 80 / 150ms，各注入 20 次，
      记「粘到的是新文本还是旧内容」。它直接吃掉端到端延迟预算里的 150ms，值得量准。
- [ ] **间歇性缺陷的残余风险**（不是待办，是跑手工时要留意的现象）：自动化那层对粘贴结果
      做了最多 4 次重试，单次失败率 p 的缺陷在那里只有 p⁴ 的概率转红。
      「有没有把 Ctrl+V 发出去」已由链上键盘钩子那条测试确定性地钉住；
      但「发出去了、目标偶尔粘不上」这一族，只有真实应用能暴露。
      现象是「十几次里有一次没上屏」，遇到请记下当时的前台应用与剪贴板监听类软件。

## 跑完必查（自动化测试也遵守同一份）

- [ ] 剪贴板内容已还原（整轮测试由 `ClipboardGuard` 兜；手工验完要自己看一眼）。
- [ ] 无孤儿窗口、无孤儿进程。
- [ ] 修饰键没有卡住：`GetAsyncKeyState` 读 Ctrl / Alt / Shift / Win 全为 0。

## 已由实测销掉、不必再人验的两项（留档，免得日后有人重新提出来）

- **持有剪贴板的进程被强杀会不会把整台机器的剪贴板挂住** —— 不会。子进程用真 HWND 持有时
  父进程 `OpenClipboard` 得 `False / err 5`，`Kill` 之后 200ms 再试得 `True / err 0`。
- **用户按住 `Win` 说话时，我们补发的 `Win↑` 会不会弹出开始菜单** —— **会**，实测 3/3
  （阳性对照「真按一放 Win」同为 3/3），随后 explorer 抢走前台、识别文本逐字进搜索框。
  已修：`BuildModifierRelease` 在 Win 的 KEYUP 之前插一个未分配码（`VK_NEUTRAL = 0xE8`）
  的按放当中间键，实测 0/3，且该键在 EDIT 里不留任何字符。
  另测得：同时按住别的修饰键时（Ctrl+Win、Ctrl+Alt+Win）本来就不弹（先发的那些 KEYUP
  自己就是中间键），故只有「Win 是唯一按住的修饰键」这一种情形中招。
