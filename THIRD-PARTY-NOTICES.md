# 第三方组件声明

lisscribe 自身的代码为 MIT，见 [LICENSE](LICENSE)。发行包里另有下列第三方组件，各自适用自己的许可。

许可全文随包分发：Apache-2.0 见 [licenses/Apache-2.0.txt](licenses/Apache-2.0.txt)，
ANGLE 的 BSD-3-Clause 见 [licenses/ANGLE-LICENSE.txt](licenses/ANGLE-LICENSE.txt)。
MIT 全文即本仓库的 `LICENSE`，其版权归各自项目所有。

**模型权重不在此列，也不随包分发**——它们的许可与本项目无关，见 README 的「许可」一节。

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
