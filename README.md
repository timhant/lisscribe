# 聆书 · Lisscribe

Windows 上的**纯离线**语音输入工具。按住热键说话，松开后识别结果直接上屏到当前焦点窗口。
不联网、不上传、不留录音。

## 快速开始

从 [Releases](https://github.com/timhant/lisscribe/releases) 下载 `lisscribe-<版本>-win-x64.zip`，
**不必装 .NET**，运行时已随包。

1. 解压到任意目录
2. 按包里 `models\README.txt` 的说明下载模型放进 `models\`（**权重不随包分发**）
3. 双击 `Lisscribe.App.exe`
4. 按住 `Ctrl + Alt + Space` 说话，松开上屏

程序常驻托盘。右键托盘图标可以打开配置窗、暂停热键、打开日志与模型目录。
热键、上屏方式、录音时长等都在配置窗里改。

## 从源码跑

1. 装 [.NET 10 SDK](https://dotnet.microsoft.com/download)
2. 取模型：`powershell -ExecutionPolicy Bypass -File scripts\fetch-models.ps1`
3. 跑：`dotnet run --project src/Lisscribe.App`

自己出发行包：`powershell -ExecutionPolicy Bypass -File scripts\publish.ps1`，产物在 `dist\`。

## 选哪档模型

|                    | SenseVoice（默认） | Qwen3-ASR 0.6B |
| ------------------ | -------------- | -------------- |
| 体积                 | 229 MB         | 982 MB         |
| 单次录音上限             | 60 秒           | 25 秒           |
| 一句 5 秒的话，多久出字      | 约 0.3 秒        | 约 3 秒          |
| 中文数字               | 「早上9点」         | 「早上九点」         |
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

## 许可

代码 **MIT**，见 [LICENSE](LICENSE)。识别引擎 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 为 Apache-2.0。

**模型权重不随本项目分发，且许可与代码不同**：SenseVoiceSmall 适用阿里 FunASR 的
`MODEL_LICENSE`（自定义条款，非 MIT 或 Apache），Qwen3-ASR 为 Apache-2.0。用之前请自己确认一遍。
