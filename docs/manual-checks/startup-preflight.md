# 手工验收：启动前置检查（StartupPreflight）

自动化测试覆盖了 `StartupPreflight` 的判断逻辑，但**弹窗、退出码、以及 `Local\` 的会话隔离
语义**只能在真机上验。下面第 1、2、4、5 条已在开发机上跑过（结论见各条末尾），
第 3 条需要两个真实登录会话，**尚未执行**。

## 前置

```powershell
dotnet build lisscribe.sln
$exe = "build\Lisscribe.App\Debug\Lisscribe.App.exe"
```

退出码约定（`Program.ExitCodeFor`）：

| 码 | 含义 |
|---|---|
| 0 | 检查通过 |
| 1 | `AlreadyRunning` —— 已有实例在跑 |
| 2 | `ProgramDirectoryNotWritable` —— 程序目录写不进去 |
| 3 | `SingleInstanceCheckFailed` —— 名字被占住了（非互斥同名对象，或 DACL 拒绝访问） |
| 4 | 未预料的异常（`Main` 顶层兜底） |
| 99 | `StartupBlockReason` 加了新值却忘了在 `ExitCodeFor` 里登记 |

> **取退出码务必用 `Start-Process -PassThru` + `WaitForExit()`。** `Lisscribe.App` 是 `WinExe`
> （GUI 子系统），直接 `& $exe` 时 PowerShell 不等它结束，`$LASTEXITCODE` 是**空的**——
> 会让人误以为"没返回码"。下面每条都给了可直接粘贴的写法，别自己简写成 `& $exe`。
>
> ```powershell
> function Invoke-Lisscribe {
>     $p = Start-Process -FilePath $exe -PassThru
>     $null = $p.WaitForExit()
>     "exit=$($p.ExitCode)"
> }
> ```
>
> **环境变量 `LISSCRIBE_HEADLESS=1` 抑制弹窗，只留退出码。** 手工验收要看弹窗，所以**不要**设它；
> 但任何脚本化/自动化的复核都必须设，否则模态对话框会把脚本挂死到超时。
> 判定是**精确匹配字符串 `"1"`**：`0`、`true`、`TRUE`、`" 1"` 一律不抑制。
>
> **每条做完都请收尾**，别把没人关的对话框留在机器上（曾经留过一个挂了 17 分钟）：
>
> ```powershell
> Get-Process Lisscribe.App -ErrorAction SilentlyContinue | Stop-Process -Force
> ```

---

## 1. 连开两次 —— 期望弹「已在运行」并以 1 退出

眼下 `Main` 检查完就返回，进程不驻留，所以"连点两次"抓不到重叠窗口。用一个外部持有者
把互斥体占住来复现（这与"第一个实例正跑着"对内核对象而言是同一件事）。

**注意：互斥体必须由另一条线程持有**——互斥体对持有它的线程可重入，在当前线程上占不住。
PowerShell 单线程写法只能靠"先弹窗、人还没点掉"这段时间差，很容易搞错，故这里改用 runspace：

```powershell
$rs = [runspacefactory]::CreateRunspace(); $rs.Open()
$ps = [powershell]::Create(); $ps.Runspace = $rs
$null = $ps.AddScript('
    $m = New-Object System.Threading.Mutex($false, "Local\lisscribe-single-instance")
    try { $m.WaitOne(0) | Out-Null; Start-Sleep -Seconds 120 }   # 占住，等手工验收做完
    finally { $m.ReleaseMutex(); $m.Dispose() }                  # 这两句缺一不可，理由见下
')
$handle = $ps.BeginInvoke()
Start-Sleep -Seconds 1                # 等它真的拿到

$p = Start-Process -FilePath $exe -PassThru   # ← 手工看弹窗，点掉它
$null = $p.WaitForExit()
"exit=$($p.ExitCode)"                          # 期望 1

$ps.Stop(); $ps.Dispose(); $rs.Dispose()

$p = Start-Process -FilePath $exe -PassThru    # 释放后再来一次
$null = $p.WaitForExit()
"exit=$($p.ExitCode)"                          # 期望 0，且无弹窗
```

**判据**

- 弹出标题为 `lisscribe`、带错误图标的对话框，正文说明已在运行，并提示"若托盘里找不到它
  （explorer 重启会吞掉图标），请在任务管理器中结束 Lisscribe.App 后重试"。
- 点掉对话框后退出码为 **1**。
- 释放互斥体后再运行，退出码为 **0**、无弹窗。

**那个 `finally` 不是讲究，是必须。** `$ps.Stop()` 结束的只是线程与 runspace，`$m` 的句柄仍开在
本 PowerShell 进程里，名字继续被一个**互斥体**占着。于是第 4 条（要用同名 **Semaphore** 占位）
在同一会话里根本摆不出局面 —— `New-Object Semaphore` 当场抛
`A WaitHandle with system-wide name ... cannot be created.`，脚本继续往下跑，得到的是 `exit=0`
而判据要 3。**一个好的构建会被判成「第 4 条不过」。** 实测（2026-07-26，同一会话按 1→4 顺序）：

```
文档原写法：  item1 exit=1   item1b exit=0   item4 占位失败 -> exit=0   ← 假红
加 try/finally：item1 exit=1   item1b exit=0   item4 exit=3            ← 全对
```

附带一层：没有 `ReleaseMutex` 时，「释放后再来一次得 0」走的其实是 `AbandonedMutexException`
分支，与判据所述的「持有者正常释放」不是同一条路 —— 加了 `finally` 之后才名实相符。

**程序能驻留之后请改回真正的「连开两次」**：第一个实例留在托盘里，双击第二次才是用户真实动作。

**已验（2026-07-26，Win11 22631 / .NET 10.0.10）：** headless 取码 1 / 0 如期。
对话框已用 `EnumWindows` 机械确认真的出现（窗口类 `#32770`，标题 `lisscribe`）。

---

## 2. 放进拒绝写入的目录后运行 —— 期望带完整路径的提示，以 2 退出

这一条同时是
`DirectoryWritabilityProbeTests.Probe_WhenPermissionDenied_ReportsPathAndDistinctReason`
的验收方式。**那条单元测试在 Windows 上是跳过的**（跳过消息自陈"此路径由 Windows 端手工验收"，
因为造只读目录要改 ACL，不是单元测试该干的事），此前无人写过验收方式 —— 就是这里。
它在 Linux 上断言的是"报告里要有完整路径，且原因与'目录不存在'截然不同（是'权限'）"，
下面第三条判据验的正是同一件事。

```powershell
$src  = "build\Lisscribe.App\Debug"
$root = Join-Path $env:TEMP ("lisscribe-ro-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root | Out-Null
# 排掉 models\：这条检查在引擎加载之前就该拦下，用不着那 239MB，拷进去只是白等。
Copy-Item -Path (Join-Path $src "*") -Destination $root -Recurse -Force -Exclude "models"

$me = "$env:USERDOMAIN\$env:USERNAME"
icacls $root /deny "${me}:(WD,AD,WA,WEA)"     # 只拒写，保留读与执行

$p = Start-Process -FilePath (Join-Path $root "Lisscribe.App.exe") -PassThru   # ← 手工看弹窗
$null = $p.WaitForExit()
"exit=$($p.ExitCode)"                          # 期望 2

icacls $root /remove:d "$me"
Remove-Item $root -Recurse -Force
```

**判据**

- 弹窗正文含**完整目录路径**。
- 原因是「权限不足」（而非「目录不存在」）—— 两种失败必须能区分开，否则用户会去建一个已经存在的目录。
- 含补救建议：「请把程序装到可写的位置（勿装在 Program Files），或为该目录授予写权限」。
- **正文中不得出现「以管理员身份运行」。** 那句话与
  `app.manifest` 自相矛盾：提权会让本进程升到高完整性级别，而 UIPI 是双向的 ——
  届时反而注入不进普通窗口。**教用户提权就是教用户把上屏功能弄坏。**
  （单元测试 `Run_WhenDirectoryDoesNotExist_ReportsNotWritableWithPath` 已断言 Message
  不含"管理员"；那条走的是"目录不存在"分支，而权限分支的措辞出自同一个 `FailureMessage`。）
- 退出码 **2**。

**已验（2026-07-26）：** 退出码 2 如期。实测出的 `FailureMessage` 原文：

```
目录不可写：C:\Users\<用户>\AppData\Local\Temp\lisscribe-ro-<guid>（权限不足：Access to the
path 'C:\...\lisscribe-ro-<guid>\.lisscribe-write-probe-<guid>' is denied.）。config.json 与
日志都存在程序目录旁，请把程序装到可写的位置（勿装在 Program Files），或为该目录授予写权限。
```

四条判据（完整路径、原因为"权限不足"、含补救建议、无"管理员"）全部满足。

> 附带发现：`FailureReason` 里嵌的是 .NET 的英文原文（`Access to the path ... is denied.`）。
> 中英混排不好看，但它带着探测文件的完整路径，排障时有用。要不要改由 T1 定，不在本任务范围。

---

## 3. `Local\` 的会话隔离语义 —— **尚未执行，需两个真实登录会话**

交接文档 §5.1 第 4 条记的账，至今无人验。`SingleInstanceGuard` 用的是 `Local\` 前缀，
其**意图**是"每个登录会话各一份"，这样远程桌面 / 快速用户切换下甲用户开着程序不会挡住乙用户。
但这条语义在 Linux 上根本没法验，在单会话的 Windows 上也验不了。

**步骤**

1. 以用户 A 登录，启动 `Lisscribe.App.exe`，让它把互斥体占住。
2. 不注销 A，用「切换用户」或另开一个远程桌面会话，以**用户 B** 登录。
3. 在 B 的会话里启动同一个 `Lisscribe.App.exe`。

**判据**

- **两边都应当能起来。** B 的退出码应为 0，不弹「已在运行」。
- 若 B 被挡住（退出码 1），说明 `Local\` 在本环境下**实为全局互斥**，与设计意图不符。
  这时应记为缺陷，改用显式的会话前缀（`Session\<id>\`）或在名字里拼进会话 ID，
  并补一条自动化测试（可用 `Process.GetCurrentProcess().SessionId` 构造）。

**反向的一半也值得顺手一验：** 同一个会话里开两个（第 1 条已覆盖）必须互斥。
只验"两个会话都能起"而不验"同会话挡得住"，等于没验出前缀到底起没起作用。

---

## 4. 名字被同名的非互斥内核对象占用 —— 期望以 3 退出，且**不是**崩溃

交接文档 §5.1 第 2 笔账。已有自动化测试
（`StartupPreflightTests.Run_WhenNameCollidesWithNonMutexObject_ReportsCheckFailedInsteadOfThrowing`，
Windows 上真跑不跳过），此处只是端到端复核一遍退出码：

```powershell
$sem = New-Object System.Threading.Semaphore(1,1,"Local\lisscribe-single-instance")
$p = Start-Process -FilePath $exe -PassThru    # ← 手工看弹窗
$null = $p.WaitForExit()
"exit=$($p.ExitCode)"                          # 期望 3
$sem.Dispose()
```

**判据**

- **不是**进程凭空消失，而是弹出一句能读懂的话：名字被另一个同名的非互斥内核对象占用，
  lisscribe 无法确认自己是否已在运行，故拒绝启动。
- 退出码 **3**（区别于 1：这不是"已有实例"，是"这道检查做不成了"）。

**已验（2026-07-26）：** 退出码 3 如期。

---

## 5. 名字被占且**拒绝访问** —— 期望以 3 退出，且**不是**崩溃

这是评审轮查出的 Critical。`new Mutex(false, "Local\...")` 在名字被同类型对象占住、
但其 DACL 不许本进程访问时，抛的是 `UnauthorizedAccessException` —— 它既不是 `IOException`
也不是 `WaitHandleCannotBeOpenedException`，修复前会一路冲出启动路径，
退出码 `-532462766`（`0xE0434352`，CLR 未捕获异常），无窗口无提示。

**真实成因：** 用户曾右键「以管理员身份运行」开过一份 lisscribe（`app.manifest` 写的是
`asInvoker`，但那拦不住用户主动提权），那份还跑着 —— 高完整性级别进程建的内核对象，
中完整性进程改不动（强制完整性等级的 NO_WRITE_UP），而 .NET 的 `Mutex` 构造请求里含
`MUTEX_MODIFY_STATE`。

PowerShell 5.1 造这个局面很别扭（`MutexAcl` 是 .NET Core 才有的），故本条用配套的外部
驱动（见下文「自动化复核」）跑。判据：

- 退出码 **3**，不是 `-532462766`。
- 弹窗正文点明「已被占用，且系统拒绝本进程访问」，并给出「最可能是另一份以管理员身份运行，
  请在任务管理器中结束那一份」。
- 正文**明确劝阻**用户改用管理员身份启动 lisscribe（理由同第 2 条：UIPI 双向）。

**已验（2026-07-26）：** 三种拒绝形状（deny `FullControl` / deny `Modify` / deny `Synchronize`）
均抛 `UnauthorizedAccessException`；修复后退出码为 3。

---

## 弹窗可见性（第 1、2、4、5 条共用）

**结论：对话框不会被别的窗口盖住，但也不会抢走焦点。** 二者都是实测定下来的。

实测方法：从一个**非前台**进程拉起 exe，取对话框中心点问 `WindowFromPoint` 谁在最上层。

| `uType` 组合 | `exStyle` | `WS_EX_TOPMOST` | 未被遮挡 |
|---|---|---|---|
| `ICONERROR` | `0x00010101` | 否 | **否** |
| `ICONERROR｜MB_TOPMOST` | `0x00010101` | 否 | **否** ← 该标志完全没起作用 |
| `ICONERROR｜MB_SETFOREGROUND｜MB_TOPMOST` | `0x00010101` | 否 | **否** |
| `ICONERROR｜MB_SYSTEMMODAL` | `0x00010108` | 是 | **是** |
| `ICONERROR｜MB_SETFOREGROUND｜MB_SYSTEMMODAL` | `0x00010108` | 是 | **是** |

故实现用的是 `MB_SYSTEMMODAL`（现代 Windows 上它不再"冻住系统"，文档明写只是加
`WS_EX_TOPMOST` 且不影响用户操作其它窗口），**不是**看起来更贴切的 `MB_TOPMOST`。

`MB_SETFOREGROUND` 在上表五种组合里 `dlgIsForeground` 全为 `False`：它只在调用方本就握有
前台权时才生效（双击启动即是），被前台锁挡住时是空操作。留着它是为了双击那条路，
但「能被看见」靠的是 `MB_SYSTEMMODAL`。

真 exe 上的复核：`exStyle=0x00010108 TOPMOST=True 未被遮挡=True dlgIsForeground=False`。

---

## 自动化复核（配套驱动）

第 1、4、5 条以及退出码矩阵可以整体自动化。本轮用的外部驱动（约 90 行 C#，`net10.0` 控制台）
做两件事，**日后的打包冒烟测试可直接复用**：

- `exitcodes <exe>`：headless 跑一遍 0 / 1 / 0 / 3 / 3 矩阵（含 deny-DACL 那条）。
  互斥体在独立线程上持有，deny-DACL 用 `MutexAcl.Create` + `MutexSecurity` 构造。
- `dialog <exe>`：非 headless 拉起，用 `FindWindowW("#32770", "lisscribe")` 定位对话框，
  查 `WS_EX_TOPMOST` 与遮挡情况，然后 `PostMessage(WM_CLOSE)` 关掉它并取退出码
  —— **自己收尾，不留孤儿弹窗**。

本轮实测输出：

```
clean                  exit=0   expect=0  OK
mutex held elsewhere   exit=1   expect=1  OK
after release          exit=0   expect=0  OK
squatted by semaphore  exit=3   expect=3  OK
squatted deny-DACL     exit=3   expect=3  OK
```

退出码 4 用临时注入异常验过（`RunStartup` 首行 `throw`），得 4；未注入时不可达。

> 驱动本身**没有入库**：它依赖 `MutexAcl` 与一堆 user32 P/Invoke，归打包那一摊，
> 此处只记下它做了什么、值得留用。

---

## 尚未覆盖

- 第 3 条整条未执行（需两个真实登录会话）。
- 弹窗的**排版细节**（字号、长消息换行、超长路径是否撑破对话框）未目视确认；
  可见性与出现与否已机械验过。
- 开机自启场景下的启动检查（工作目录是 system32）—— 未验。
- `MB_SETFOREGROUND` 在**握有前台权**时的实际效果未验（本环境构造不出那个前提）。
