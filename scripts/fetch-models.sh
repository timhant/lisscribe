#!/usr/bin/env bash
# 下载识别模型到 models/<档位>/。
#
#   scripts/fetch-models.sh                # 默认档 SenseVoice -> models/sense-voice
#   scripts/fetch-models.sh qwen3          # 高精度档 Qwen3-ASR -> models/qwen3-asr
#   scripts/fetch-models.sh qwen3 /some/dir
#
# 模型权重不入 Git：SenseVoiceSmall 适用阿里 FunASR MODEL_LICENSE（非 Apache/MIT），
# Qwen3-ASR 为 Apache-2.0，两者都不得打入发行包。
#
# 幂等：可反复执行。已就位的文件一律不动（下载 1GB 和覆盖用户手动放置的模型同样不可接受）。
# 三条路径，按代价从小到大：
#   1. 目标目录已完整          → 直接退出；
#   2. models/ 下已解包官方目录 → 就地链接/复制，不联网；
#   3. 都没有                  → 才下载。
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENGINE="${1:-sense-voice}"

case "${ENGINE}" in
  sense-voice|sensevoice|SenseVoice)
    ENGINE="sense-voice"
    ARCHIVE="sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17"
    # 与 AsrModelPaths 的常量保持一致。
    REQUIRED=("model.int8.onnx" "tokens.txt")
    ONNX_FILES=("model.int8.onnx")
    # 官方 int8 模型约 229MB。下限取 200MB：低于此必是截断。
    MIN_ONNX_BYTES=209715200
    ;;
  qwen3|qwen3-asr|Qwen3Asr)
    ENGINE="qwen3-asr"
    ARCHIVE="sherpa-onnx-qwen3-asr-0.6B-int8-2026-03-25"
    # 与 Qwen3ModelPaths 的常量保持一致。tokenizer 是目录，单列在 REQUIRED_DIRS。
    REQUIRED=("conv_frontend.onnx" "encoder.int8.onnx" "decoder.int8.onnx" "tokenizer/vocab.json")
    ONNX_FILES=("conv_frontend.onnx" "encoder.int8.onnx" "decoder.int8.onnx")
    # 最小的那个 conv_frontend.onnx 约 42MB，下限取 32MB。
    MIN_ONNX_BYTES=33554432
    ;;
  *)
    echo "未知档位 \"${ENGINE}\"，只支持 sense-voice / qwen3。" >&2
    exit 2
    ;;
esac

DEST="${2:-${REPO_ROOT}/models/${ENGINE}}"
URL="https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models/${ARCHIVE}.tar.bz2"

is_complete() {
  local dir="$1" f
  for f in "${REQUIRED[@]}"; do
    [ -f "${dir}/${f}" ] || return 1
  done
  return 0
}

# 廉价初筛：大小下限 + ONNX（protobuf）首字节。ONNX 没有魔数，但正常导出的模型
# 第一个字段必是 ir_version（tag 0x08）；下成 HTML 错误页时这里就会露馅。
# 真正决定性的那道在运行时（*ModelPaths.DescribeCorruption 遍历顶层 protobuf 字段，
# 能抓出少 0.1% 尾巴的截断）。这里只求让故障在「取模型」时暴露，而不是拖到首次启动。
screen_model() {
  local dir="$1" f size head_byte
  for f in "${ONNX_FILES[@]}"; do
    size="$(wc -c < "${dir}/${f}")"
    if [ "${size}" -lt "${MIN_ONNX_BYTES}" ]; then
      echo "校验失败：${dir}/${f} 只有 ${size} 字节，远小于预期 —— 多半是下载或拷贝中断。" >&2
      return 1
    fi

    head_byte="$(od -An -N1 -tu1 < "${dir}/${f}" | tr -d '[:space:]')"
    if [ "${head_byte}" != "8" ]; then
      echo "校验失败：${dir}/${f} 的文件头不是 ONNX（protobuf）格式，可能下到的是错误页而非模型。" >&2
      return 1
    fi
  done

  for f in "${REQUIRED[@]}"; do
    if [ ! -s "${dir}/${f}" ]; then
      echo "校验失败：${dir}/${f} 是空文件。" >&2
      return 1
    fi
  done

  return 0
}

# 失效符号链接：目标被删或被移走后，DEST 这个名字还在，但 -e 判否、mkdir -p 又会报
# 「File exists」。这种裸报错完全不指向真正的问题，故显式识别并说人话。
# 断链本身不含任何数据，直接移除不算覆盖用户的东西。
if [ -L "${DEST}" ] && [ ! -e "${DEST}" ]; then
  echo "注意：${DEST} 是失效的符号链接（指向 $(readlink "${DEST}")，该目标已被删除或移动），移除后重新获取。" >&2
  rm -f "${DEST}"
fi

# 从已解包的官方目录填充目标目录。绝不覆盖已存在的文件（-n），
# 以免把用户自己换进去的模型/词表悄悄换掉。
populate_from() {
  local src="$1" f
  mkdir -p "${DEST}"
  for f in "${REQUIRED[@]}"; do
    mkdir -p "$(dirname "${DEST}/${f}")"
    cp -rn "${src}/$(echo "${f}" | cut -d/ -f1)" "${DEST}/" 2>/dev/null || true
  done
  [ -d "${src}/test_wavs" ] && cp -rn "${src}/test_wavs" "${DEST}/"             2>/dev/null || true
  [ -f "${src}/LICENSE" ]   && cp -n  "${src}/LICENSE"   "${DEST}/MODEL_LICENSE" 2>/dev/null || true
  return 0
}

if is_complete "${DEST}"; then
  if screen_model "${DEST}"; then
    echo "模型已完整存在于 ${DEST}，跳过。"
    exit 0
  fi

  # 不替用户删掉几百 MB —— 万一那是他有意放的东西。只把该做什么说清楚。
  echo "请删除 ${DEST} 后重跑本脚本重新获取。" >&2
  exit 1
fi

# 官方压缩包解包后就叫 ${ARCHIVE}/。若已在 models/ 下（人工下载或上次运行留下的），
# 直接用它，别为了换个目录名再拉一遍。
LOCAL_EXTRACTED="${REPO_ROOT}/models/${ARCHIVE}"
if is_complete "${LOCAL_EXTRACTED}"; then
  # 优先做符号链接：复制一份纯属浪费磁盘（Qwen3 那份 982MB），且日后更新易出现两份不一致。
  # 链接失败（Windows 非管理员、跨文件系统等）再退回复制。
  if [ ! -e "${DEST}" ] && ln -s "${LOCAL_EXTRACTED}" "${DEST}" 2>/dev/null; then
    echo "已链接 ${DEST} -> ${LOCAL_EXTRACTED}（未下载）。"
  else
    echo "发现已解包的 ${LOCAL_EXTRACTED}，就地复制到 ${DEST}（未下载）..."
    populate_from "${LOCAL_EXTRACTED}"
  fi

  is_complete "${DEST}" || { echo "填充 ${DEST} 后仍不完整，请检查权限。" >&2; exit 1; }
  screen_model "${DEST}" || { echo "源目录 ${LOCAL_EXTRACTED} 里的模型本身就是坏的。" >&2; exit 1; }
  echo "完成。模型位于 ${DEST}"
  exit 0
fi

mkdir -p "$(dirname "${DEST}")"
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT

echo "下载 ${ARCHIVE}.tar.bz2 ..."
curl -fL --retry 3 -C - -o "${TMP}/model.tar.bz2" "${URL}"

# 解包本身就是最强的一道下载校验：bzip2 每块带 CRC，tar 也校验头部，
# 下载被截断或字节损坏在这里就会失败（set -e 会让脚本停住），坏包不会落到 DEST。
echo "解包 ..."
tar xjf "${TMP}/model.tar.bz2" -C "${TMP}"

populate_from "${TMP}/${ARCHIVE}"

is_complete "${DEST}" || { echo "解包后 ${DEST} 仍不完整，压缩包结构可能已变。" >&2; exit 1; }
screen_model "${DEST}" || { echo "解包出的模型未通过校验，上游包可能已变更。" >&2; exit 1; }

echo "完成。模型位于 ${DEST}"

if [ "${ENGINE}" = "sense-voice" ]; then
  echo "注意：模型权重适用阿里 FunASR MODEL_LICENSE，与本项目的 MIT 代码许可不同，不得打入发行包。"
else
  echo "注意：Qwen3-ASR 权重为 Apache-2.0，与本项目的 MIT 代码许可不同，同样不得打入发行包。"
fi
