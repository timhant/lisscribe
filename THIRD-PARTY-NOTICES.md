# 第三方组件声明

lisscribe 自身的代码为 MIT，见 [LICENSE](LICENSE)。发行包里另有下列第三方组件，各自适用自己的许可。

许可全文随包分发：Apache-2.0 见 [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)，
ANGLE 的 BSD-3-Clause 见 [licenses/ANGLE-LICENSE.txt](licenses/ANGLE-LICENSE.txt)，
FunASR 模型开源协议见 [licenses/FunASR-MODEL_LICENSE.txt](licenses/FunASR-MODEL_LICENSE.txt)。
MIT 全文即本仓库的 `LICENSE`，其版权归各自项目所有。

**模型权重只在「带模型」的那两个发行包里**，精简包不含权重。权重的许可与代码的 MIT
不是一回事，见下面「模型权重」一节。

## Apache-2.0

| 组件 | 项目 |
| --- | --- |
| org.k2fsa.sherpa.onnx（含 `sherpa-onnx-c-api.dll`、`onnxruntime.dll`） | https://github.com/k2-fsa/sherpa-onnx |
| Serilog.Extensions.Logging.File 及其依赖的 Serilog、Serilog.Extensions.Logging、Serilog.Formatting.Compact、Serilog.Sinks.Async、Serilog.Sinks.File、Serilog.Sinks.RollingFile | https://serilog.net |

## BSD-3-Clause

| 组件 | 项目 |
| --- | --- |
| Avalonia.Angle.Windows.Natives（ANGLE，包里名为 `av_libglesv2.dll`） | https://chromium.googlesource.com/angle/angle |

Copyright 2018 The ANGLE Project Authors. 条款与免责声明全文见上述文件。

## MIT

| 组件 | 项目 |
| --- | --- |
| Avalonia、Avalonia.Desktop、Avalonia.Themes.Fluent 及其运行时组件（Avalonia.Win32、Avalonia.Skia、Avalonia.Native、Avalonia.X11、Avalonia.FreeDesktop、Avalonia.Remote.Protocol、Avalonia.BuildServices、MicroCom.Runtime、Tmds.DBus.Protocol） | https://avaloniaui.net |
| SkiaSharp、HarfBuzzSharp 及其原生资源包 | https://github.com/mono/SkiaSharp |
| NAudio、NAudio.Core 及其子包（NAudio.Wasapi、NAudio.WinMM、NAudio.Asio、NAudio.Midi、NAudio.WinForms） | https://github.com/naudio/NAudio |
| CommunityToolkit.Mvvm | https://github.com/CommunityToolkit/dotnet |
| Microsoft.Extensions.Logging、Microsoft.Extensions.Logging.Console、Microsoft.Extensions.DependencyInjection 及其依赖 | https://github.com/dotnet/runtime |
| Microsoft.Win32.SystemEvents | https://github.com/dotnet/runtime |
| .NET 10 运行时（self-contained 发布，运行时文件随包） | https://github.com/dotnet/runtime |

## 模型权重

只有「带模型」的发行包含权重，精简包不含。两档权重的许可各不相同，**都不是 MIT**。
ONNX 格式的转换版由 [sherpa-onnx](https://github.com/k2-fsa/sherpa-onnx) 的
`asr-models` 发行页提供，上游权利人如下表。

| 模型 | 上游 | 许可 |
| --- | --- | --- |
| SenseVoiceSmall（包里目录 `models\sense-voice\`） | 阿里巴巴集团 / FunAudioLLM，https://huggingface.co/FunAudioLLM/SenseVoiceSmall | FunASR Model Open Source License Agreement v1.1 |
| Qwen3-ASR-0.6B（包里目录 `models\qwen3-asr\`） | 阿里巴巴集团 / Qwen，https://huggingface.co/Qwen/Qwen3-ASR-0.6B | Apache-2.0 |

**SenseVoiceSmall 适用的是自定义协议，不是 Apache 也不是 MIT**，全文见
[licenses/FunASR-MODEL_LICENSE.txt](licenses/FunASR-MODEL_LICENSE.txt)。它允许自由使用、
复制、修改和分享（§2.1），条件是**注明出处与作者信息，并保留模型名称**（§2.2）——本节与包内
`models\sense-voice\` 这个目录名即为履行该义务。另有两条不同于常见开源许可的条款，
用它之前请自己读一遍：**§4.2 对该软件的「无端诋毁、恶意抹黑或凭空谩骂」将被视为自动放弃
本协议下的全部许可**；**§6 协议可由权利人不时修订并自动生效**。§3 声明该软件「仅作为参考和
学习使用」，且不承担任何直接或间接损失。

Qwen3-ASR-0.6B 为 Apache-2.0，全文即上面那份
[licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)。

Copyright (C) 2023-2028 Alibaba Group. All rights reserved.

## 怎么核对

上表按包名分组。若要逐个复核某个包的许可，读它的 `.nuspec`：

```powershell
$root = (dotnet nuget locals global-packages --list).Split(' ')[-1]
[xml]$x = Get-Content "$root\<包名小写>\<版本>\<包名小写>.nuspec"
$x.package.metadata.license
```

依赖清单以 `Directory.Packages.props` 与各 `.csproj` 的 `PackageReference` 为准；
有一条测试（`ReleaseArtifactsTests.TheThirdPartyNotices_NameEveryDirectDependency`）
盯着「新加了依赖却忘了写进这里」。
