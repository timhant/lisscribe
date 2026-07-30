# 聆书 · Lisscribe

Windows 上的**纯离线**语音输入工具。按住热键说话，松开后识别结果直接上屏到当前焦点窗口。
不联网、不上传、不留录音。

## 现状

**0.1.0，第一个公开版本。** 主链路已通：按住热键说话 → 松开识别 → 文本上屏，配上悬浮条、
托盘菜单与配置窗；两档识别引擎可切换。**尚无预编译发行包**，请自行构建（见下）。

接下来是输入器的完备度：热词、中文数字 ITN、自定义热键的边界情况。

## 快速开始

1. 装 [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. 取模型：`pwsh scripts/fetch-models.ps1`（默认档 SenseVoice，229MB）
3. 跑：`dotnet run --project src/Lisscribe.App`
4. 按住 `Ctrl + Alt + Space` 说话，松开上屏

想用高精度档：`pwsh scripts/fetch-models.ps1 -Engine qwen3`（982MB），然后在托盘「打开配置」里
把识别引擎换成 `Qwen3Asr`。**换档要重启程序**，程序会弹窗问一句。

## 模型

**模型放在 exe 旁边，开发与发行同一套布局。**

```
build\Lisscribe.App\Debug\
├── Lisscribe.App.exe
├── models\sense-voice\{model.int8.onnx, tokens.txt}
└── models\qwen3-asr\{conv_frontend.onnx, encoder.int8.onnx, decoder.int8.onnx, tokenizer\}
```

配置项 `asr.modelDir` 的默认值（`./models/sense-voice`）按**程序所在目录**、而不是当前工作
目录展开——开机自启时工作目录是 `system32`，按它展开必然找不到模型。

改路径有三层，优先级从高到低：

| | 何时用 |
|---|---|
| 环境变量 `LISSCRIBE_MODEL_DIR` | 逃生口：模型放别处、多份副本共用一份权重。设了就不回落 |
| `asr.modelDir`（配置窗里有输入框，托盘也能直接「打开模型目录」） | 常规改法 |
| 默认 `./models/<档位>` | 什么都不设时 |

同一个变量也管着测试里那些真模型用例（`ModelFixture`）——设一次，两边都认。

开发时把仓库根的 `models\` 拷进输出目录即可：

```powershell
$dst = "build\Lisscribe.App\Debug\models\sense-voice"
New-Item -ItemType Directory -Force -Path $dst | Out-Null
Copy-Item "models\sense-voice\*" -Destination $dst -Recurse -Force
```

## 构建输出

统一落在仓库根的 `build\<项目名>\<配置>\`（`Directory.Build.props` 里的 `BaseOutputPath`），
TFM 与 RID 两层目录已砍掉。`config.json` 与 `logs\` 也在那儿——本项目一律放程序目录旁，
不做 `%APPDATA%` 回退。

发行包用 `pwsh scripts/publish.ps1` 生成到 `dist\`，随后按
`docs/clean-machine-checklist.md` 在一台干净机器上验收。

## 两档识别引擎

|  | SenseVoice（默认） | Qwen3-ASR 0.6B（高精度） |
|---|---|---|
| 体积 | 229MB | 982MB |
| 5.6 秒语音识别耗时 | P50 284ms / P95 613ms | P50 2.7s / P95 3.1s |
| **单次录音上限** | **60 秒** | **25 秒** |
| 中文数字 | 「早上9点」（有 ITN） | 「早上九点」（**没有，且补不上**） |
| 粤语 / 中英混说 / 嘈杂数字 | 弱 | 强 |

耗时是本机实测（Release，4 线程），换台机器要重测——取数用具是
`tests/Lisscribe.Core.Tests/Asr/EndToEndLatencyBenchmark.cs`。

## 已知限制

- **仅 Windows。** 全局热键与文本注入是平台原生能力；Linux 在 Wayland 下从设计上禁止全局
  键盘钩子与合成按键，绕不过去。
- **单次录音有上限，两档不同**：SenseVoice 60 秒、Qwen3 25 秒。到点自动结束并正常识别，
  悬浮条会在最后 5 秒倒数。Qwen3 那个 25 秒不是模型的算术上限（512 token 约合 38 秒），
  而是**实测它撞满之前很久就开始静默丢字**，宁可少录。
- **焦点在管理员权限窗口**（任务管理器、管理员 PowerShell、regedit）时，UIPI 会挡住热键与
  注入。程序会明确提示并把文本转入剪贴板，**不静默丢字**。
- **explorer.exe 重启后托盘图标不会自己回来**（Avalonia 未处理 `TaskbarCreated` 广播），
  需重启程序。
- **只有 CPU 推理。** sherpa-onnx 官方 NuGet 是纯 CPU 构建，其附带的 `onnxruntime.dll` 只导出
  CPU provider，**DirectML 未被编入**（传 `"directml"` 只会静默回落 CPU）；GPU 加速仅有
  CUDA 一条路，作为可选项留待后续。
- **第三方输入法未核。** 只在微软拼音下验过。

## 许可

**代码与模型权重的许可不同，请分别遵守。**

- **代码**：MIT（见 `LICENSE`）
- **sherpa-onnx**：Apache-2.0，与 MIT 兼容
- **SenseVoiceSmall 权重**：适用阿里 FunASR 的 `MODEL_LICENSE`——自定义条款，**不是** Apache
  或 MIT，要求署名并保留模型名，另含若干非标准条款
- **Qwen3-ASR 权重**：Apache-2.0

**本项目不分发任何模型权重**，只提供下载脚本。发行包里的 `models\` 目录只有一份说明。
