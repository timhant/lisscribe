# 下载识别模型到 models/<档位>/。Windows 侧的取模型脚本，与 fetch-models.sh 一一对应。
#
#   pwsh scripts/fetch-models.ps1                      # 默认档 SenseVoice -> models/sense-voice
#   pwsh scripts/fetch-models.ps1 -Engine qwen3        # 高精度档 Qwen3-ASR -> models/qwen3-asr
#   pwsh scripts/fetch-models.ps1 -Engine qwen3 -Destination D:\models\qwen3
#
# **为什么不是只留那个 .sh**：本项目是 Windows-only 的，而干净的 Windows 机器上没有 bash。
# 「按说明放入模型」是干净机验收清单里的一步，指向一个跑不了的脚本等于那一步走不通。
#
# 模型权重不入 Git：SenseVoiceSmall 适用阿里 FunASR MODEL_LICENSE（非 Apache/MIT），
# Qwen3-ASR 为 Apache-2.0，两者都不得打入发行包。
#
# 幂等：可反复执行。已就位的文件一律不动（下载 1GB 和覆盖用户手动放置的模型同样不可接受）。
# 三条路径，按代价从小到大：
#   1. 目标目录已完整          -> 直接退出；
#   2. models/ 下已解包官方目录 -> 就地复制，不联网；
#   3. 都没有                  -> 才下载。

[CmdletBinding()]
param(
    [ValidateSet('sense-voice', 'qwen3')]
    [string]$Engine = 'sense-voice',

    [string]$Destination
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot

# 文件名与 AsrModelPaths / Qwen3ModelPaths 的常量一一对应，
# 有一条自动化测试（ReleaseArtifactsTests）拿代码常量来核对这里，改名会当场翻红。
if ($Engine -eq 'sense-voice') {
    $dirName = 'sense-voice'
    $archive = 'sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17'
    $required = @('model.int8.onnx', 'tokens.txt')
    $onnxFiles = @('model.int8.onnx')
    # 官方 int8 模型约 229MB。下限取 200MB：低于此必是截断。
    $minOnnxBytes = 209715200
    $licenceNote = '模型权重适用阿里 FunASR MODEL_LICENSE，与本项目的 MIT 代码许可不同，不得打入发行包。'
}
else {
    $dirName = 'qwen3-asr'
    $archive = 'sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25'
    # tokenizer 是**目录**不是文件——Test-Path 对目录返回真，但下面按文件逐个查，
    # 故这里列的是 tokenizer/vocab.json 而不是 tokenizer。
    $required = @('conv_frontend.onnx', 'encoder.int8.onnx', 'decoder.int8.onnx', 'tokenizer/vocab.json')
    $onnxFiles = @('conv_frontend.onnx', 'encoder.int8.onnx', 'decoder.int8.onnx')
    # 最小的那个 conv_frontend.onnx 约 42MB，下限取 32MB。
    $minOnnxBytes = 33554432
    $licenceNote = 'Qwen3-ASR 权重为 Apache-2.0，与本项目的 MIT 代码许可不同，同样不得打入发行包。'
}

if (-not $Destination) {
    $Destination = Join-Path $repoRoot "models\$dirName"
}

$url = "https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/$archive.tar.bz2"

function Test-ModelComplete {
    param([string]$Dir)

    foreach ($f in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $f) -PathType Leaf)) {
            return $false
        }
    }

    return $true
}

# 廉价初筛：大小下限 + ONNX（protobuf）首字节。ONNX 没有魔数，但正常导出的模型
# 第一个字段必是 ir_version（tag 0x08）；下成 HTML 错误页时这里就会露馅。
# 真正决定性的那道在运行时（*ModelPaths.DescribeCorruption 遍历顶层 protobuf 字段，
# 能抓出少 0.1% 尾巴的截断）。这里只求让故障在「取模型」时暴露，而不是拖到首次启动。
function Test-ModelSane {
    param([string]$Dir)

    foreach ($f in $onnxFiles) {
        $path = Join-Path $Dir $f
        $size = (Get-Item -LiteralPath $path).Length

        if ($size -lt $minOnnxBytes) {
            Write-Warning "校验失败：$path 只有 $size 字节，远小于预期 —— 多半是下载或拷贝中断。"
            return $false
        }

        # 只读第一个字节，不会把 756MB 读进内存。**用 FileStream 而不是 Get-Content**：
        # 后者读字节的参数在 Windows PowerShell 5.1 上是 -Encoding Byte、在 PowerShell 7 上
        # 换成了 -AsByteStream，写哪个都会在另一边报错，而干净机器上默认只有 5.1。
        $stream = [System.IO.File]::OpenRead($path)
        try {
            $head = $stream.ReadByte()
        }
        finally {
            $stream.Dispose()
        }

        if ($head -ne 8) {
            Write-Warning "校验失败：$path 的文件头不是 ONNX（protobuf）格式，可能下到的是错误页而非模型。"
            return $false
        }
    }

    foreach ($f in $required) {
        $path = Join-Path $Dir $f
        if ((Get-Item -LiteralPath $path).Length -eq 0) {
            Write-Warning "校验失败：$path 是空文件。"
            return $false
        }
    }

    return $true
}

# 从已解包的官方目录填充目标目录。**绝不覆盖已存在的文件**，以免把用户自己换进去的
# 模型或词表悄悄换掉。
function Copy-ModelFrom {
    param([string]$Source)

    New-Item -ItemType Directory -Force -Path $Destination | Out-Null

    # 取每条 required 的第一段：tokenizer/vocab.json 要整个 tokenizer 目录跟过来。
    $topLevel = $required | ForEach-Object { ($_ -split '/')[0] } | Select-Object -Unique

    foreach ($item in $topLevel + @('test_wavs')) {
        $from = Join-Path $Source $item
        $to = Join-Path $Destination $item

        if ((Test-Path -LiteralPath $from) -and -not (Test-Path -LiteralPath $to)) {
            Copy-Item -LiteralPath $from -Destination $to -Recurse
        }
    }

    # 官方包里的 LICENSE 是**权重的**许可，与仓库根那份 MIT 不是一回事，故改名放进去。
    $licence = Join-Path $Source 'LICENSE'
    $licenceTo = Join-Path $Destination 'MODEL_LICENSE'
    if ((Test-Path -LiteralPath $licence) -and -not (Test-Path -LiteralPath $licenceTo)) {
        Copy-Item -LiteralPath $licence -Destination $licenceTo
    }
}

if (Test-ModelComplete $Destination) {
    if (Test-ModelSane $Destination) {
        Write-Host "模型已完整存在于 $Destination，跳过。"
        exit 0
    }

    # 不替用户删掉几百 MB —— 万一那是他有意放的东西。只把该做什么说清楚。
    Write-Error "请删除 $Destination 后重跑本脚本重新获取。"
}

# 官方压缩包解包后就叫 $archive\。若已在 models\ 下（人工下载或上次运行留下的），
# 直接用它，别为了换个目录名再拉一遍。
#
# **这里不做符号链接**（.sh 那边会先试 ln -s）：Windows 上 New-Item -ItemType SymbolicLink
# 要开发者模式或管理员，改用 junction 就得自己 P/Invoke 还得管生命周期。
# 多占一份磁盘，换脚本能在任何一台机器上跑通。
$localExtracted = Join-Path $repoRoot "models\$archive"

if (Test-ModelComplete $localExtracted) {
    Write-Host "发现已解包的 $localExtracted，就地复制到 $Destination（未下载）..."
    Copy-ModelFrom $localExtracted
}
else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("lisscribe-model-" + [System.Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null

    try {
        $tarball = Join-Path $temp 'model.tar.bz2'

        Write-Host "下载 $archive.tar.bz2 ..."

        # 用系统自带的 curl.exe 而不是 Invoke-WebRequest：后者在 PowerShell 5.1 上会把整个
        # 响应读进内存再落盘（1GB 的包足以让它难看），也没有断点续传。
        # Win10 1803+ 自带 curl.exe 与 tar.exe，两者都不必额外安装。
        & curl.exe -fL --retry 3 -C - -o $tarball $url
        if ($LASTEXITCODE -ne 0) {
            throw "下载失败（curl 退出码 $LASTEXITCODE）：$url"
        }

        # 解包本身就是最强的一道下载校验：bzip2 每块带 CRC，tar 也校验头部，
        # 下载被截断或字节损坏在这里就会失败，坏包不会落到目标目录。
        # 用 -xf 让 bsdtar 自己认压缩格式，不写 -j（GNU tar 的写法，bsdtar 上不通用）。
        Write-Host "解包 ..."
        & tar.exe -xf $tarball -C $temp
        if ($LASTEXITCODE -ne 0) {
            throw "解包失败（tar 退出码 $LASTEXITCODE），压缩包可能已损坏。"
        }

        Copy-ModelFrom (Join-Path $temp $archive)
    }
    finally {
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }
}

if (-not (Test-ModelComplete $Destination)) {
    Write-Error "$Destination 仍不完整，压缩包结构可能已变，或当前用户对该目录没有写权限。"
}

if (-not (Test-ModelSane $Destination)) {
    Write-Error "$Destination 里的模型未通过校验，上游包可能已变更。"
}

Write-Host ""
Write-Host "完成。模型位于 $Destination"
Write-Host "注意：$licenceNote"
