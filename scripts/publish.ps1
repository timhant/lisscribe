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
$outputRoot = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repoRoot $Output }

# 版本号只有一个出处。**别在这儿另写一份**：两处各写各的，早晚出现「zip 名上是一个版本、
# 里面的 exe 报另一个」，而拿到包的人只信文件名，报错时会照着错的版本去查。
[xml]$props = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Encoding UTF8
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version }).Version

if (-not $version) {
    throw 'Directory.Build.props 里没有 <Version>，发行包无从命名。'
}

# 解压出来是一个带名字的目录，不是把三百多个文件铺进用户的下载文件夹。
$packageName = "lisscribe-$version-win-x64"
$outputPath = Join-Path $outputRoot $packageName
$archivePath = Join-Path $outputRoot "$packageName.zip"

Remove-Item -Recurse -Force $outputRoot -ErrorAction SilentlyContinue

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

# 压成一个归档，那才是能挂到 Release 上、能发给别人的东西。
# 条目名统一加上 $packageName 前缀：解压后是一个目录，而不是把三百多个文件铺进下载文件夹。
#
# **条目名必须用正斜杠，而现成的两个 API 都不行**：Windows PowerShell 5.1 跑在 .NET Framework
# 上，Compress-Archive 与 ZipFile.CreateFromDirectory 写出来的都是反斜杠（实测 337 个条目全是）。
# Windows 资源管理器与 7-Zip 忍得下，换到 macOS / Linux 解压就摊成一堆名字里带 \ 的文件，
# 目录结构整个没了。故这里逐个建条目，自己把分隔符换掉。有测试开包验这一点。
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue

$archive = [System.IO.Compression.ZipFile]::Open(
    $archivePath, [System.IO.Compression.ZipArchiveMode]::Create)

try {
    foreach ($file in Get-ChildItem -LiteralPath $outputPath -Recurse -File) {
        $relative = $file.FullName.Substring($outputPath.Length + 1).Replace('\', '/')

        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $file.FullName,
            "$packageName/$relative",
            [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

if (-not (Test-Path -LiteralPath $archivePath)) {
    throw "打包失败：没有生成 $archivePath。"
}

$archiveSizeMb = [math]::Round((Get-Item -LiteralPath $archivePath).Length / 1MB, 1)

Write-Host ""
Write-Host "发布完成：$outputPath"
Write-Host "发行包：  $archivePath（$archiveSizeMb MB）"
Write-Host "下一步：按 docs\clean-machine-checklist.md 在一台干净机器上验收。"
