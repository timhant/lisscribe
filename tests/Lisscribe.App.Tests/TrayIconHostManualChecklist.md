# 托盘手工验收清单

**先说清楚这份清单为什么这么短。** 托盘这一块看着「只能手工验」，实际搬进自动化的有十一条
（`TrayMenuModelTests` 5 条 + `TrayIconHostTests` 6 条）。

留在这里的只有两类：**真需要人眼的**（长什么样、弹出来的版式），和
**只在进程退出时才现形的**（avalonia#7588）。

---

## 已自动化的部分（不必人验，列在这里是为了别重复劳动）

| 事项 | 由哪条测试盯着 |
|---|---|
| 图标资源在、能加载 | `TheIconAsset_IsThereAndLoads`（带阴性对照：不存在的名字必须抛） |
| 菜单六项齐全、项序正确 | `Build_WhenRunning_ListsTheSixCommandsInTheOrderTheSpecGives` |
| 分隔符位置 | `Build_PutsSeparatorsAroundTheDiagnosticGroup` |
| 「打开模型目录」文案点明是模型目录 | `Build_NamesTheModelFolderItem_...`（带阴性对照：全菜单只此一处提「模型」） |
| 每条命令调到各自那个动作 | `TrayIconHostDispatchTests.Invoke_RoutesEachCommandToItsOwnAction` |
| 「暂停热键」↔「恢复热键」文案切换（#8751 绕行） | `Build_WhenPaused_ChangesTheTextOfExactlyOneItem` |
| **没有任何地方用勾选标记**（#8751 绕行别被人改回去） | `Attach_SetsNoCheckMarksAnywhere`（变异实测：给暂停项加 `ToggleType=CheckBox` 即红 1 条） |
| 状态一变整份菜单重建（#8076 绕行） | `TogglePause_CallsPauseThenResume_AndRebuildsTheWholeMenu`（变异实测：删掉 `RebuildMenu()` 即红 1 条） |
| 托盘提示文字随状态变 | 同上 + `TooltipFor_SaysSoWhenTheHotkeyIsPaused` |
| 暂停/恢复真的调到了控制器 | 同上（`["pause"]` → `["pause","resume"]`） |
| 动作抛异常时不逃逸、且状态不翻 | `Invoke_WhenTheActionThrows_KeepsTheMenuHonest_AndDoesNotEscape`（变异实测：把翻状态挪到动作之前即红 1 条） |
| 退出前先隐藏图标（#7588 绕行的前半） | `Detach_HidesTheIcon_...`（变异实测：删掉 `IsVisible = false` 即红 1 条） |
| 图标从 Application 上摘干净、Detach 可重入 | 同上 |

---

## A. 只能人眼看的

- [ ] **A1 图标长什么样。** 启动后系统托盘出现 lisscribe 图标（深色圆角底 + 白色话筒）。
      在**浅色**与**深色**任务栏下各看一眼：底色是 `#1E1E1E`，浅色任务栏下不该糊成一团。
- [ ] **A2 菜单版式。** 右键弹出，八行：打开配置 / 暂停热键 / ── / 打开模型目录 /
      打开日志目录 / 关于 lisscribe / ── / 退出。两条分隔线要看得见（自动化只验了数据结构里
      有分隔项，验不了它在屏幕上画出来没有）。
- [ ] **A3 文案不截断。** 「恢复热键」「打开日志目录」在 125%/150% 缩放下不被省略号截掉。

## B. 只在进程退出时才现形的

- [ ] **B1 退出不崩。** 点「退出」→ 进程正常结束，**不弹崩溃对话框**，退出码为 0。
      这是 avalonia#7588 的真正回报。**自动化只验到「隐藏这一步做了」，验不到「隐藏发生在
      Dispose 之前」**——实测 `Dispose()` 自己不动 `IsVisible` 也不发通知，dispose 之后再设
      也不抛，故这两步的先后在进程内观察不到。
      → **打包冒烟测试应当把这条接过去**：起真 exe、走退出路径、看退出码。
- [ ] **B2 退出后托盘里不留幽灵图标**（鼠标扫过托盘区域，图标不该「扫一下才消失」）。

## C. 已知限制，验一次记一次

- [ ] **C1 explorer.exe 重启后图标不回来。** 任务管理器结束 explorer.exe 再重启它 →
      托盘图标消失且**不会**自己回来（Avalonia 未处理 `TaskbarCreated` 广播）。
      日志里应有 Attach 时那句「已知限制」。此时再启动一次 lisscribe →
      提示「已有实例在运行」并退出（单实例互斥生效，退出码 1）。
- [ ] **C2 没有气泡通知。** Avalonia 的托盘没有这个 API，报警一律走悬浮条。这条不必操作，
      只是提醒：谁哪天想加气泡提示，先确认 Avalonia 有没有补上。

## D. 接线相关（须把整个程序跑起来验）

- [ ] **D1 左键单击图标 → 打开配置窗。**
      自动化验不了这一条：`TrayIcon.Clicked` 是事件，外部无法引发。
- [ ] **D2 暂停状态下按热键无反应，恢复后正常。**
- [ ] **D3 「打开日志目录」真的打开了资源管理器。**
- [ ] **D4 「打开模型目录」真的打开了资源管理器，且目录不存在时先建出来。**
      把 `asr.modelDir` 指到一个不存在的路径、重启、点这一项 → 该目录被建出来并打开。
      这一项正是给「模型没放对地方」的人准备的，那时目录多半还不存在。
      自动化只验到「点了会调到那个动作」与「动作抛异常不带走进程」，
      验不了 `Process.Start` 在真机上做了什么。
