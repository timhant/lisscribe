# Lisscribe · 聆书

**Offline voice typing for Windows.** Hold a hotkey, talk, let go — the text lands in whatever
window has focus. No network at runtime, no account, no audio kept.

[![CI](https://github.com/timhant/lisscribe/actions/workflows/ci.yml/badge.svg)](https://github.com/timhant/lisscribe/actions/workflows/ci.yml)
[![Licence: MIT](https://img.shields.io/badge/licence-MIT-blue.svg)](LICENSE)
![Windows x64](https://img.shields.io/badge/platform-Windows%20x64-lightgrey)

*[中文说明在下面](#聆书--windows-上的纯离线语音输入)*

---

## What it does

You are typing in any app — an editor, a chat box, a terminal, a browser form. Instead of typing:

1. Hold `Ctrl + Alt + Space`
2. Say your sentence
3. Let go — the recognised text is typed into that same window

That's the whole interaction. Lisscribe sits in the tray and does nothing until you press the key.

## Why you might want this one

- **It never talks to the network.** Recognition runs locally, on the CPU. Once the model is on
  disk, you can unplug the machine and it still works. Nothing you say leaves the computer.
- **It works in every window**, not just apps that opted into a dictation API — the text goes in
  through synthesised keystrokes (or a clipboard paste, your choice).
- **Nothing is written down.** Audio is never saved. Recognised text is never written to disk or
  into the log — the log records how many characters landed, never what they were.
- **Good with Chinese**, including Cantonese and mixed Chinese/English, depending on which model
  tier you pick. English works too.
- **No .NET install needed.** The runtime ships inside the zip.

Not for you if: you need macOS or Linux, or you want real-time streaming captions.

## Quick start

Three downloads on the [Releases](https://github.com/timhant/lisscribe/releases) page — pick one:

| Download | Size | What's inside |
| --- | --- | --- |
| `lisscribe-<version>-win-x64-sense-voice.zip` | ~218 MB | App **and** the default model. Unzip, run, done. |
| `lisscribe-<version>-win-x64-qwen3.zip` | ~871 MB | App and the high-accuracy model (better at Cantonese, mixed Chinese/English, noise). |
| `lisscribe-<version>-win-x64.zip` | ~65 MB | App only. Fetch a model yourself, per `models\README.txt`. |

Then:

1. Unzip anywhere
2. Run `Lisscribe.App.exe`
3. Hold `Ctrl + Alt + Space`, talk, release

It lives in the tray. Right-click for settings, to pause the hotkey, or to open the log and model
folders. Hotkey, text-delivery method and recording limit are all configurable.

The bundled weights keep their own licences, which are **not** this project's MIT — one of them is
not an open-source licence at all. Full texts ship inside those zips; see [Licence](#licence).

## Which model tier

|                                    | SenseVoice (default) | Qwen3-ASR 0.6B |
| ---------------------------------- | -------------------- | -------------- |
| Download size                      | 229 MB               | 982 MB         |
| Max length of one recording        | 60 s                 | 25 s           |
| Time to text, for a 5-second phrase | ~0.3 s               | ~3 s           |
| Chinese numerals                   | "早上9点", built into the model | "早上9点", converted by Lisscribe, narrower coverage |
| Cantonese / mixed CN-EN / noisy    | weaker               | stronger       |

Timings are measured on one machine and will differ on yours.

To get the high-accuracy tier:
`powershell -ExecutionPolicy Bypass -File scripts\fetch-models.ps1 -Engine qwen3`,
then switch the engine to `Qwen3Asr` in settings. **Switching tiers needs a restart**; the app
will ask.

## Where the models go

Next to the executable, under `models\`:

```
Lisscribe.App.exe
models\sense-voice\{model.int8.onnx, tokens.txt}
models\qwen3-asr\{conv_frontend.onnx, encoder.int8.onnx, decoder.int8.onnx, tokenizer\}
```

To keep them elsewhere, change "model directory" in settings, or set the environment variable
`LISSCRIBE_MODEL_DIR` (the variable wins).

## Build from source

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. Fetch models: `powershell -ExecutionPolicy Bypass -File scripts\fetch-models.ps1`
3. Run: `dotnet run --project src/Lisscribe.App`

To produce a release zip: `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1` — output
lands in `dist\`.

## Licence

Code is **MIT**, see [LICENSE](LICENSE). The recognition engine,
[sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx), is Apache-2.0.

**Model weights are licensed separately from the code.** SenseVoiceSmall falls under Alibaba
FunASR's Model Open Source License Agreement v1.1 — custom terms, **neither** MIT nor Apache. It
does permit redistribution, which is why the bundled download exists, on condition that source and
author are attributed and the model name kept; it also carries clauses you will not find in a
standard open-source licence (§4.2 revokes your licence for "denigration" of the software; §6 lets
the terms be revised unilaterally). Qwen3-ASR is Apache-2.0. Full texts ship inside the bundled
zips and live here as [`licenses/FunASR-MODEL_LICENSE.txt`](licenses/FunASR-MODEL_LICENSE.txt) and
[`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt); read them before you rely on either model.

---

# 聆书 · Windows 上的纯离线语音输入

按住热键说话，松开后识别结果直接上屏到当前焦点窗口。运行期不联网、不上传、不留录音。

## 怎么用

你正在任何一个窗口里打字——编辑器、聊天框、终端、网页表单。不打字，改成：

1. 按住 `Ctrl + Alt + Space`
2. 说一句话
3. 松手——识别出的文字打进刚才那个窗口

交互就这些。平时它待在托盘里，不按热键就什么也不做。

## 凭什么用它

- **运行期一次网络都不碰。**识别在本机 CPU 上跑，模型落盘之后拔了网线照样能用——
  你说的话不出这台机器。
- **在任何窗口都能用**，不限于自己接了听写接口的应用：文字是靠合成按键送进去的
  （也可以改走剪贴板粘贴）。
- **什么都不记下来。**音频从不保存，识别结果既不落盘也不进日志——日志只记这次上了几个字，
  从不记是哪几个字。
- **中文够用**，粤语与中英混说看你选哪一档模型。英文也认。
- **不必装 .NET**，运行时已在包里。

不适合你，如果：你要 macOS 或 Linux；你要实时滚动字幕。

## 快速开始

[Releases](https://github.com/timhant/lisscribe/releases) 页上有三个包，挑一个：

| 下载 | 体积 | 里面有什么 |
| --- | --- | --- |
| `lisscribe-<版本>-win-x64-sense-voice.zip` | 约 218 MB | 程序**加**默认档模型。解压就能用。 |
| `lisscribe-<版本>-win-x64-qwen3.zip` | 约 871 MB | 程序加高精度档模型（粤语、中英混说、嘈杂环境更准）。 |
| `lisscribe-<版本>-win-x64.zip` | 约 65 MB | 只有程序，模型按包里 `models\README.txt` 自己取。 |

然后：

1. 解压到任意目录
2. 双击 `Lisscribe.App.exe`
3. 按住 `Ctrl + Alt + Space` 说话，松开上屏

程序常驻托盘。右键托盘图标可以打开配置窗、暂停热键、打开日志与模型目录。
热键、上屏方式、录音时长等都在配置窗里改。

随包的权重各有各的许可，与本项目的 MIT **不是一回事**，其中一档**根本不是开源许可**。
许可全文就在那两个包里，另见下面「许可」。

## 选哪档模型

|                    | SenseVoice（默认） | Qwen3-ASR 0.6B |
| ------------------ | -------------- | -------------- |
| 体积                 | 229 MB         | 982 MB         |
| 单次录音上限             | 60 秒           | 25 秒           |
| 一句 5 秒的话，多久出字      | 约 0.3 秒        | 约 3 秒          |
| 中文数字               | 「早上9点」，引擎自带    | 「早上9点」，本程序转，覆盖面窄些 |
| 粤语 / 中英混说 / 嘈杂环境   | 弱              | 强              |

耗时是本机实测，换台机器会变。

取高精度档：`powershell -ExecutionPolicy Bypass -File scripts\fetch-models.ps1 -Engine qwen3`，
然后在配置窗里把识别引擎换成 `Qwen3Asr`。**换档要重启程序**，程序会问你一句。

## 模型放哪儿

模型放在 exe 旁边的 `models\` 里：

```
Lisscribe.App.exe
models\sense-voice\{model.int8.onnx, tokens.txt}
models\qwen3-asr\{conv_frontend.onnx, encoder.int8.onnx, decoder.int8.onnx, tokenizer\}
```

想放别处，就在配置窗里改「模型目录」，或设环境变量 `LISSCRIBE_MODEL_DIR`（后者优先）。

## 从源码跑

1. 装 [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. 取模型：`powershell -ExecutionPolicy Bypass -File scripts\fetch-models.ps1`
3. 跑：`dotnet run --project src/Lisscribe.App`

自己出发行包：`powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`，产物在 `dist\`。

## 许可

代码 **MIT**，见 [LICENSE](LICENSE)。识别引擎 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 为 Apache-2.0。

**模型权重的许可与代码不是一回事。**SenseVoiceSmall 适用阿里 FunASR 模型开源协议 v1.1，
自定义条款，**既不是 MIT 也不是 Apache**。它确实允许再分发（带模型的包因此才存在），条件是
注明出处与作者、保留模型名称；但它还带着常见开源许可里没有的条款——§4.2 对该软件的「无端
诋毁、恶意抹黑或凭空谩骂」将被视为自动放弃全部许可，§6 允许权利人单方修订协议并自动生效。
Qwen3-ASR 为 Apache-2.0。许可全文随带模型的包走，仓库里则是
[`licenses/FunASR-MODEL_LICENSE.txt`](licenses/FunASR-MODEL_LICENSE.txt) 与
[`licenses/Apache-2.0.txt`](licenses/Apache-2.0.txt)；依赖哪一档之前请自己读一遍。
