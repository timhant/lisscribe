# WASAPI 录音手工验收清单

**先说清楚这份清单**不**包含什么。** 本任务里最容易错的三件事——字节怎么解释、两个声道怎么合、
重采样比例对不对——全是纯数据变换，已经整条从 COM 管道里剥进 `CaptureSamplePipeline`
并用构造出来的已知波形精确单测。收尾次序那件真设备上没法确定性复现的事，用照真
`WasapiCapture` 实测时序做的假件 `FakeWaveIn` 钉住。故下列各项**已经自动化**，不在本清单里重复：

| 已自动化的行为 | 测试 |
|---|---|
| float32 字节逐个精确解成样本 | `Process_Float32Bytes_DecodesEachFourBytesAsOneSample` |
| PCM16 按 `s / 32768` 换算（含 ±满程边界） | `Process_Pcm16Bytes_ScalesEachShortByOneOver32768` |
| **48kHz 立体声 → 16kHz 单声道**，样本数误差 <1% | `Process_From48kStereo_YieldsExactlyOneThirdAsManyMonoSamples` |
| 44.1kHz 同理 | `Process_From44100Stereo_YieldsTheRatioThatSampleRateImplies` |
| 左右声道取平均，不是只取一路 | `Process_From48kStereo_AveragesTheTwoChannelsRatherThanPickingOne` |
| **波形本身没被弄坏**：200Hz 正弦进、200Hz 出，RMS 不变 | `Process_A200HzSine_ComesOutStillAt200HzWithTheSameAmplitude` |
| 认不出的格式当场抛，且消息里带格式 | `Constructor_WithAFormatWeCannotDecode_ThrowsInsteadOfGuessing` |
| 认不出的格式不会留下一个开着的设备 | `Start_WithAFormatWeCannotDecode_ThrowsAndLeavesNothingOpen` |
| 残帧不会凭空多出样本（**注意**：这**不是**「守卫」的回归钉子，见下方「已知无覆盖」） | `Process_WithATrailingPartialFrame_DoesNotProduceAnExtraSample` |
| `Flush` 不凭空多造样本 | `Flush_CalledTwice_DoesNotManufactureExtraSamples` |
| **收尾时在途那一包不丢**（丢的话是尾巴上的 60–100ms） | `Stop_WithAPacketStillInFlight_DeliversItInsteadOfDroppingTheTail` |
| `Stop()` 返回后一个样本也不再来 | `Stop_AfterItReturns_NoMoreSamplesArrive` |
| `Dispose()` 期间不再往订阅者发音频 | `Dispose_WhileRecording_StopsTheDeviceWithoutRaisingMoreSamples` |
| 采集回调里订阅者抛异常被咽下并记账 | `DataAvailable_WhenTheSubscriberThrows_IsSwallowedSoRecordingKeepsRunning` |
| `RecordingStopped` 带异常时记账且不外逃 | `RecordingStopped_WithAnException_IsLoggedAndDoesNotEscape` |
| 设备变更通知只认 Capture+Console | `OnDefaultDeviceChanged_ForTheCaptureConsoleRole_RaisesTheEvent` / `..._ForAnyOtherFlowOrRole_IsIgnored` |
| 通知订阅者抛异常不断流、留 Error 日志 | `OnDefaultDeviceChanged_WhenTheSubscriberThrows_KeepsDeliveringAndLogsIt` |
| `Start()` 重入只开一个设备 | `Start_CalledTwice_OnlyOpensOneDevice` |
| 没 Start 就 Stop / 重复 Dispose / Dispose 后 Start | `Stop_WithoutStart_DoesNotThrow` / `Dispose_Twice_DoesNotThrow` / `Start_AfterDispose_Throws` |
| `Dispose()` 之后设备名照样读得出 | `CurrentDeviceName_AfterDispose_StillWorks` |
| **真设备**上速率确为 ~16000 样本/秒 | `Start_WithTheRealDefaultDevice_DeliversRoughly16000SamplesPerSecond` |
| **真设备**上反复 Start/Stop 不抛 | `StartStop_RepeatedlyOnTheRealDevice_DoesNotThrow` |
| **COM 通知线程不被订阅者阻塞**（阻塞即整机挂死） | `OnDefaultDeviceChanged_ReturnsWithoutWaitingForTheSubscriber` |
| 挡在 `Invoke` 前面的日志抛出也不吞事件 | `OnDefaultDeviceChanged_WhenLoggingItselfThrows_TheEventStillReachesSubscribers` |
| `RecordingStopped` 里记日志失败也不逃逸 | `RecordingStopped_WhenLoggingTheFailureItselfThrows_NothingEscapesTheCaptureThread` |
| 采集线程持续送包时**样本一个不丢**（真并发下的记账） | `Stop_WhileTheCaptureThreadIsStillPushingPackets_AccountsForEverySample` |
| PCM16 连送不同大小的两包不重放上一包尾巴 | `Process_TwoPcm16PacketsOfDifferentSizes_DoesNotReplayTheFirstPacketsTail` |
| 64bit float / 认不出的 Extensible SubFormat 一律拒 | `Constructor_WithSixtyFourBitFloat_…` / `Constructor_WithAnUnrecognisedExtensibleSubFormat_Throws` |
| 查不到设备时给占位串并记 Warning | `CurrentDeviceName_WhenTheLookupThrows_GivesThePlaceholderAndLogsIt` |

`FakeWaveIn` 模拟的三条时序本身是**探针在真 `WasapiCapture` 上量出来的**，不是假件自己规定的
（Win11 22631 / .NET 10 / NAudio 2.3.0 / Realtek Microphone Array）：

- `StopRecording()` 只置标志就返回（0.0–0.2ms），此时采集线程可能还有一包在途。
  **这个窗口真实存在，但命不命中取决于相位**：本任务在「录 600ms 后停」这一个相位上五轮 5/5
  命中，独立复评把它自 600 扫到 800ms（21 个相位）命中 15/21，而在它自己那个相位上重跑 5 轮
  **0/5**。别当必现，也别因为一次没复现就当它不存在。
- `Dispose()` 会 Join 采集线程（本任务量到订阅者睡 700ms 时阻塞 667–684ms，
  独立复评同条件 526.6–559.5ms；Join 坐实，数字随相位浮动），
  **但只在采集线程还活着时**：NAudio 在 `RaiseRecordingStopped` **之前**就把 `captureThread`
  置 null，故自停路径（订阅者在 `DataAvailable` 抛异常、拔设备、驱动出错）上它**一次 Join 也不做**
  ——实测自停后让 `RecordingStopped` 睡 800ms，`Dispose()` **19.9ms 就返回了**。
  实际成立的保证只有「`Dispose()` 返回后不会再有 `DataAvailable`」这一条。
- `RecordingStopped` 在采集线程上引发（构造 `WasapiCapture` 的线程无 `SynchronizationContext` 时）。

## 已知无覆盖（别把它们当"已验"）

- **残帧守卫（`CaptureSamplePipeline.Process` 里的 `% _bytesPerFrame`）没有可观测效果，
  没有任何测试杀得掉移除它的变异。** 独立复评穷举 8 种格式 × 12 种字节余数 × 3 个包大小逐样对拍，
  结论是 `guard has zero observable effect`，且数学上必然（`floor(bytes/8)` 与
  `floor(floor(bytes/4)/2)` 恒等）。守卫留着是为了守 `AudioResampler` 的入参契约，不是有测试兜着。
  上一版这里写的「不把左右声道错位」是一个**已被推翻的说法**，别再照它验收。
- **`Flush()` 恒空**，故 `CaptureSession.Stop` 里 `emit(tail)` 是死代码，
  `Flush_CalledTwice_…` 实际只重复验了一遍总量区间。
- **`Flush()`/`Process()` 的并发损坏无人钉住。**
  `Stop_WhileTheCaptureThreadIsStillPushingPackets_AccountsForEverySample` 盯的是**样本记账**
  （对照变异「隔一包丢一包」→ 红，Actual 正好是期望的一半），
  但把 `Flush()+emit` 提到 `Dispose` 之前制造真并发，它**连跑 4 轮全绿**——
  那是单发竞争，一次运行只有一次撞上的机会，没有哪种判据能可靠转红。
  那件事只靠 `CaptureSession.Stop` 的代码次序与注释守着。
- **并发引发**：`DefaultDeviceChanged` 现在可能在多条线程池线程上并发引发
  （复评连投 8 条量到并发进入数 = 8），没有测试钉住订阅者串行化这件事——那是订阅者的责任。

---

## 只能人验的

本机只有**一个** Active 采集端点（`Microphone Array (Realtek(R) Audio)`），所以凡是需要
「换一个录音设备」的都无法自动化——`IPolicyConfig::SetDefaultEndpoint` 也没有第二个可切的目标。
（默认**播放**设备有两个，本任务用它验过通知链的线程、并发与异常行为，但那不是 Capture 那一路。）

**连带一条要记住**：生产代码按 `Capture + Console` 过滤，而这台机器只有一个采集端点，
所以**在这台机器上任何真实的设备变更都到不了 `DeliverDefaultDeviceChanged`**。
下面第 1、3 条因此在此机永远验不了，须换一台有两个以上采集设备（或一副带麦耳机）的机器。

### 1. 真插拔耳机 → 状态机收到设备变更

- [ ] 插一副带麦的 USB / 蓝牙耳机，让 Windows 把它选为默认录音设备。
- [ ] 期望：日志里出现 `默认录音设备已变更为 {DeviceId}`，且**只出现一次**
      （我们只认 Capture + Console；Windows 会把各 Flow/Role 挨个通知一遍——实测切一次
      默认播放设备就来 7 条）。
- [ ] 拔掉，重复一次。

### 2. 录音中途拔设备 → 已录部分不丢（**本清单里风险最高的一条**）

这条走的是 COM 通知线程那条路——「通知线程上同步收尾导致整机挂死」就出在这里，
修完之后**只在 Render 默认设备变更上做过等价验证**（COM 回调 2ms 返回、收尾在线程池上跑完，
2/2），真拔一次采集设备仍未验。

- [ ] 按住热键说一句话，**说到一半**拔掉当前录音设备。
- [ ] 期望：录音立刻收尾并送去识别，上屏的是**拔之前**说的那半句，
      悬浮条提示带「录音设备已变更」。
- [ ] **重点看程序有没有卡住**：整个进程（含托盘、悬浮条）必须照常响应；
      系统的「声音设置」窗口也必须照常响应——挂住时它会一起卡（audiosrv 的 LPC 被占）。
- [ ] 反例（须确认不发生）：整句被「录音过短」吞掉、或程序卡在 Recording。
- [ ] **另外两条易被误判的情形**，遇到别当 bug 报也别当 pass 放过：
  - 拔掉之后**没有替补设备**（系统变成「无录音设备」）：此时 Windows 未必发
    `OnDefaultDeviceChanged`，走的可能是 `OnDeviceStateChanged` —— 而那四个方法本实现是空的。
    采集流会静默死掉，只留一条「录音异常终止」日志，状态机要等看门狗或 KEYUP 才收尾。
  - 只是**禁用/启用**设备而非拔插：同上，多半只有 `OnDeviceStateChanged`。

### 3. 切换默认录音设备（需要第二个采集设备）

- [ ] 在「声音设置 → 输入」里把默认输入设备换成另一个。
- [ ] 期望：同第 1 条。空闲时切换只应留日志，不应有任何界面反应。

### 4. 录音时长与实际语音对得上

- [ ] 按住热键，对着秒表念 10 秒。
- [ ] 期望：日志里这次会话的样本数约 160000（±5%）。**别只看识别结果**——比例错 10%
      会让识别照常出字，只是内容变差，而样本数是直接读数。

### 5. 共享模式确实没抢占设备

- [ ] 一边放音乐 / 开着会议软件的麦克风预览，一边按住热键录音。
- [ ] 期望：音乐不断、会议软件的麦克风电平照常跳动，两边都能同时用麦克风。
- [ ] 这是选共享模式而不是独占模式的**全部理由**，值得每次改动录音层后复验一次。

### 6. 设备被别的程序独占时

- [ ] 用一个会以**独占模式**开麦克风的程序占住设备，然后按热键。
- [ ] 期望：`Start()` 抛出被 `KeyboardHook` 的派发线程兜住并记 Error 日志，
      **进程不崩**，状态机停在 Idle（不会卡在 Recording）。
- [ ] 已知不足：此时**没有任何界面提示**，用户只会看到「按了没反应」
      ——根子是 `IAudioCapture.Start` 没有失败通路。
