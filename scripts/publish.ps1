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
    [string]$Output = 'dist',

    # 带哪一档权重。none 出精简包（~65MB），另两档出开箱即用的包。
    # 一次只出一个，三个都要就跑三次——dotnet publish 是增量的，后两次很快。
    [ValidateSet('none', 'sense-voice', 'qwen3')]
    [string]$Model = 'none'
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
# 带模型的包在名字里说清带的是哪一档：下载页上三个 zip 并排，光有版本号分不出谁是谁。
$suffix = if ($Model -eq 'none') { '' } else { "-$Model" }
$packageName = "lisscribe-$version-win-x64$suffix"
$outputPath = Join-Path $outputRoot $packageName
$archivePath = Join-Path $outputRoot "$packageName.zip"

# **只清自己这一份，不清整个 dist。** 三档要连着跑三次，清整个目录会把前两次的产物删掉，
# 而那正是要一起挂到 Release 上的东西。
Remove-Item -Recurse -Force $outputPath -ErrorAction SilentlyContinue
Remove-Item -Force $archivePath -ErrorAction SilentlyContinue

# 带模型这一档先把权重找齐再动手：编译 + 拷 300MB 之后才发现模型不在，白等一遍。
$modelSource = $null

if ($Model -ne 'none') {
    $modelDirName = if ($Model -eq 'sense-voice') { 'sense-voice' } else { 'qwen3-asr' }
    $modelSource = Join-Path $repoRoot "models\$modelDirName"

    $modelRequired = if ($Model -eq 'sense-voice') {
        @('model.int8.onnx', 'tokens.txt')
    }
    else {
        @('conv_frontend.onnx', 'encoder.int8.onnx', 'decoder.int8.onnx',
          'tokenizer\vocab.json', 'tokenizer\merges.txt', 'tokenizer\tokenizer_config.json')
    }

    # **按清单拷，不整目录拷。** 上游包里还躺着 test_wavs\（示例音频，qwen3 那档 12.5MB）
    # 与 README.md，对用户毫无用处，只是让每个下载的人多等一会儿。
    # MODEL_LICENSE 是上游随权重发的许可原件，有就带上——署名与保留出处正是那份协议的要求；
    # 不同版本的官方包不一定有它，故不列为必需。
    $modelOptional = if ($Model -eq 'sense-voice') { @('MODEL_LICENSE') } else { @('README.md') }

    foreach ($file in $modelRequired) {
        if (-not (Test-Path -LiteralPath (Join-Path $modelSource $file) -PathType Leaf)) {
            throw "打带模型的包，但 $modelSource 里缺 $file。先跑 scripts\fetch-models.ps1 取模型。"
        }
    }
}

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

# FunASR 那份模型协议只对带 SenseVoice 权重的包才有意义。精简包与 Qwen3 包里留着它，
# 只会让人以为包里有那档权重。Qwen3 权重是 Apache-2.0，用的是上面那份 Apache-2.0.txt。
if ($Model -ne 'sense-voice') {
    Remove-Item -LiteralPath (Join-Path $outputPath 'licenses\FunASR-MODEL_LICENSE.txt') `
        -Force -ErrorAction SilentlyContinue
}
elseif (-not (Test-Path -LiteralPath (Join-Path $outputPath 'licenses\FunASR-MODEL_LICENSE.txt'))) {
    throw '带 SenseVoice 权重却没有 FunASR 模型协议全文 —— 分发时缺许可文件是许可事故。'
}

New-Item -ItemType Directory -Force -Path (Join-Path $outputPath 'models') | Out-Null

# 带模型这一档：按清单把权重拷进去。**这是分发行为**，许可义务见上面那几行与 THIRD-PARTY-NOTICES.md。
if ($Model -ne 'none') {
    $modelTarget = Join-Path $outputPath "models\$modelDirName"

    foreach ($file in ($modelRequired + $modelOptional)) {
        $from = Join-Path $modelSource $file

        if (-not (Test-Path -LiteralPath $from -PathType Leaf)) {
            continue    # 必需项在上面已经拦过一轮，走到这里还缺的只能是可选项。
        }

        $to = Join-Path $modelTarget $file

        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $to) | Out-Null
        Copy-Item -LiteralPath $from -Destination $to
    }

    # 拷完再验一遍。Copy-Item 撞上磁盘满、路径过长时会**部分成功**，
    # 而那种包解压出来只缺一个文件，用户看到的是「模型损坏」。
    foreach ($file in $modelRequired) {
        $path = Join-Path $modelTarget $file

        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "权重没拷全：$file 不在包里。"
        }

        Write-Host "OK  models\$modelDirName\$file"
    }
}

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

两者都与本程序的 MIT 代码许可不同，请分别遵守。带权重的发行包里附有对应的许可全文
（见 licenses\ 目录与 THIRD-PARTY-NOTICES.md），精简包不含权重。
'@

# 带模型的包里，上面那句「不随本程序分发」当场自相矛盾。开头补一段说清现状，
# 后面那份仍然有用——它讲的是「想换另一档怎么取」。
if ($Model -ne 'none') {
    $bundled = if ($Model -eq 'sense-voice') { '默认档 SenseVoice' } else { '高精度档 Qwen3-ASR' }

    $modelsReadme = @"
本包已带上$bundled 的权重，就在 models\$modelDirName\ 里，开箱即用，不必再下载。

下面这份说明是给「想换成另一档」的人看的。

"@ + $modelsReadme
}

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

$what = if ($Model -eq 'none') { '不带权重' } else { "带 $Model 权重" }

Write-Host ""
Write-Host "发布完成：$outputPath（$what）"
Write-Host "发行包：  $archivePath（$archiveSizeMb MB）"
Write-Host "下一步：按 docs\clean-machine-checklist.md 在一台干净机器上验收。"
