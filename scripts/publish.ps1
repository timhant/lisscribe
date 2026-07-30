# 发布 lisscribe 到 dist/ 目录。
#
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Configuration Debug -Output out
#
# 用 powershell（5.1）而非 pwsh：后者要另装，干净的 Windows 上没有。本脚本两边都跑得起来。
#
# **刻意不用单文件**（PublishSingleFile=false）：sherpa-onnx-c-api.dll 由 .NET 的 DllImport
# 解析器加载（单文件下能从解压目录找到），但它依赖的 onnxruntime.dll 是**传递依赖**，
# 由 Windows OS loader 按标准 DLL 搜索顺序加载，不经过 .NET 解析器。单文件在开发机上能跑
# （输出目录里恰好有那个 DLL），换台干净机器就是 DllNotFoundException / 0xC000007B ——
# 而这类问题永远在交付前一天才暴露。

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Output = 'dist'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$outputPath = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repoRoot $Output }

Remove-Item -Recurse -Force $outputPath -ErrorAction SilentlyContinue

& dotnet publish (Join-Path $repoRoot 'src\Lisscribe.App\Lisscribe.App.csproj') `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    --output $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败（退出码 $LASTEXITCODE）。"
}

# 校验原生依赖确实被带出来了。**这三个缺一个，干净机器上就是启动即崩**，
# 而开发机上永远复现不出来。有一条自动化测试盯着这份清单（ReleaseArtifactsTests）。
$required = @('onnxruntime.dll', 'sherpa-onnx-c-api.dll', 'Lisscribe.App.exe')

foreach ($file in $required) {
    $path = Join-Path $outputPath $file

    if (-not (Test-Path -LiteralPath $path)) {
        throw "发布产物缺少 $file —— 在干净机器上必然启动失败。"
    }

    Write-Host "OK  $file"
}

# 许可随包走。代码 MIT 那份是仓库根的 LICENSE；**权重许可不在这儿**，
# 因为权重根本不进包（见下面那份说明）。
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $outputPath 'LICENSE')

# 第三方许可**是分发义务，不是客套**：包里带着 sherpa-onnx（Apache-2.0）与 ANGLE（BSD-3-Clause）
# 的二进制，两者都要求分发时附上许可全文与版权声明。只在 README 里提一句不算数。
Copy-Item -LiteralPath (Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md') `
    -Destination (Join-Path $outputPath 'THIRD-PARTY-NOTICES.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') `
    -Destination (Join-Path $outputPath 'licenses') -Recurse

foreach ($file in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses\Apache-2.0.txt', 'licenses\ANGLE-LICENSE.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputPath $file))) {
        throw "发布产物缺少 $file —— 分发时缺许可文件是许可事故。"
    }

    Write-Host "OK  $file"
}

# 模型目录只放说明，**绝不打包权重**。两档都要写：许可不同，目录也不同。
New-Item -ItemType Directory -Force -Path (Join-Path $outputPath 'models') | Out-Null

$modelsReadme = @'
模型权重不随本程序分发，请自行下载后放到这里。两档任选其一即可，都放也行。

默认档 SenseVoice（229MB，快，中文数字会写成阿拉伯数字，单次可录 60 秒）

  models\sense-voice\model.int8.onnx
  models\sense-voice\tokens.txt

  https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17.tar.bz2

高精度档 Qwen3-ASR（982MB，慢，粤语与中英混说更准，单次只可录 25 秒）

  models\qwen3-asr\conv_frontend.onnx
  models\qwen3-asr\encoder.int8.onnx
  models\qwen3-asr\decoder.int8.onnx
  models\qwen3-asr\tokenizer\vocab.json

  https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25.tar.bz2

也可以在有开发环境的机器上跑 scripts\fetch-models.ps1，它会下载、解包并放到正确的位置。

许可（两档不同，请分别遵守）：

  SenseVoiceSmall 权重  适用阿里 FunASR MODEL_LICENSE（自定义条款，不是 Apache 或 MIT），
                        要求署名并保留模型名，另含若干非标准条款。
  Qwen3-ASR 权重        Apache-2.0。

两者都与本程序的 MIT 代码许可不同，故不随程序分发。
'@

# 显式 UTF8：Set-Content 默认按系统 ANSI 代码页写，这份说明里全是中文，
# 换台机器打开就是乱码。
Set-Content -Path (Join-Path $outputPath 'models\README.txt') -Value $modelsReadme -Encoding utf8

Write-Host ""
Write-Host "发布完成：$outputPath"
Write-Host "下一步：按 docs\clean-machine-checklist.md 在一台干净机器上验收。"
