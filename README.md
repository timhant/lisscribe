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

Not for you if: you need macOS or Linux, you want real-time streaming captions, or you want a
turnkey download — the speech model is a separate, one-time download (see below for why).

## Quick start

Grab `lisscribe-<version>-win-x64.zip` from
[Releases](https://github.com/timhant/lisscribe/releases).

1. Unzip anywhere
2. Fetch a model as described in `models\README.txt` — **model weights are not bundled**
3. Run `Lisscribe.App.exe`
4. Hold `Ctrl + Alt + Space`, talk, release

It lives in the tray. Right-click for settings, to pause the hotkey, or to open the log and model
folders. Hotkey, text-delivery method and recording limit are all configurable.

**Why isn't the model in the zip?** The weights carry their own licences, separate from this
project's MIT — and one of them is not an open-source licence at all (see [Licence](#licence)).
Redistributing them inside the release would put you and me on the wrong side of those terms.

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

**Model weights are not distributed with this project and are licensed differently from the
code**: SenseVoiceSmall falls under Alibaba FunASR's `MODEL_LICENSE` (custom terms — **neither**
MIT nor Apache), and Qwen3-ASR is Apache-2.0. Check them yourself before use.

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

不适合你，如果：你要 macOS 或 Linux；你要实时滚动字幕；你要下载完即用——语音模型是单独下的
一次性步骤（下面说了为什么）。

## 快速开始

从 [Releases](https://github.com/timhant/lisscribe/releases) 下载 `lisscribe-<版本>-win-x64.zip`。

1. 解压到任意目录
2. 按包里 `models\README.txt` 的说明下载模型放进 `models\`（**权重不随包分发**）
3. 双击 `Lisscribe.App.exe`
4. 按住 `Ctrl + Alt + Space` 说话，松开上屏

程序常驻托盘。右键托盘图标可以打开配置窗、暂停热键、打开日志与模型目录。
热键、上屏方式、录音时长等都在配置窗里改。

**为什么模型不打进包里？**那些权重各有各的许可，与本项目的 MIT 不是一回事，其中一档
**根本不是开源许可**（见下面「许可」）。把它们塞进发行包分发，你我都站在那些条款的对面。

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

**模型权重不随本项目分发，且许可与代码不同**：SenseVoiceSmall 适用阿里 FunASR 的
`MODEL_LICENSE`（自定义条款，非 MIT 或 Apache），Qwen3-ASR 为 Apache-2.0。用之前请自己确认一遍。
