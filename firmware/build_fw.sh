#!/bin/bash
# Builds Keychron Q6 Max ANSI firmware (keychron keymap) with the WinKeyGuard indicator, no sudo needed.
set -euo pipefail

ROOT="$HOME/qmk-build"
TC_VER=13.3.rel1
TC_DIR="$ROOT/arm-gnu-toolchain-$TC_VER-x86_64-arm-none-eabi"
REPO="$ROOT/keychron_qmk"
OUT_DIR="$(cd "$(dirname "$0")" && pwd)"   # this folder; the .bin is copied back here
KEYMAP_SRC="$OUT_DIR/keymap.c"

mkdir -p "$ROOT"
cd "$ROOT"

if [ ! -x "$TC_DIR/bin/arm-none-eabi-gcc" ]; then
    echo "== Downloading Arm GNU toolchain $TC_VER"
    curl -fsSL -o tc.tar.xz "https://developer.arm.com/-/media/Files/downloads/gnu/$TC_VER/binrel/arm-gnu-toolchain-$TC_VER-x86_64-arm-none-eabi.tar.xz"
    tar -xf tc.tar.xz
    rm tc.tar.xz
fi
export PATH="$TC_DIR/bin:$PATH"
arm-none-eabi-gcc --version | head -1

if [ ! -d "$REPO/.git" ]; then
    echo "== Cloning Keychron qmk_firmware (2025q3)"
    git clone --depth 1 --branch 2025q3 https://github.com/Keychron/qmk_firmware.git "$REPO"
fi
cd "$REPO"
echo "== Submodules"
git submodule update --init --recursive --depth 1

if [ ! -d "$ROOT/venv" ]; then
    echo "== Python venv"
    python3 -m venv "$ROOT/venv"
fi
source "$ROOT/venv/bin/activate"
pip install -q -r requirements.txt qmk
export QMK_HOME="$REPO"

echo "== Copying keymap"
cp "$KEYMAP_SRC" keyboards/keychron/q6_max/ansi_encoder/keymaps/keychron/keymap.c

echo "== Building"
# DFU_SUFFIX=true skips dfu-suffix (from dfu-util, not installed); QMK Toolbox flashes the .bin without it.
make -j"$(nproc)" keychron/q6_max/ansi_encoder:keychron DFU_SUFFIX=true

mkdir -p "$OUT_DIR"
cp -v keychron_q6_max_ansi_encoder_keychron.bin "$OUT_DIR/"
echo "== DONE"
