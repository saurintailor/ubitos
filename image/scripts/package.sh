#!/bin/sh
# Turns a built image into a release artifact for one host:
#   mac      -> dist/ubit-mac-arm64-<version>.img.zst       (raw disk for Virtualization.framework)
#   windows  -> dist/ubit-windows-x86_64-<version>.vhdx.zst (dynamic VHDX for Hyper-V Gen 2)
# Each artifact gets a .sha256 file next to it.
set -eu
profile=${1:?usage: package.sh mac|windows}
cd "$(dirname "$0")/.."
version=$(sed -n 's/^ImageVersion=//p' mkosi.conf)
raw=$(find mkosi.output -maxdepth 1 -type f -name '*.raw' | head -n 1)
[ -n "$raw" ] || { echo "no .raw image in mkosi.output/" >&2; exit 1; }
mkdir -p dist

case "$profile" in
    mac)
        out="dist/ubit-mac-arm64-$version.img"
        cp --reflink=auto "$raw" "$out"
        ;;
    windows)
        out="dist/ubit-windows-x86_64-$version.vhdx"
        qemu-img convert -p -f raw -O vhdx -o subformat=dynamic "$raw" "$out"
        ;;
    *) echo "unknown profile: $profile" >&2; exit 1 ;;
esac

zstd -T0 -10 --rm -f "$out"
(cd dist && sha256sum "$(basename "$out").zst" > "$(basename "$out").zst.sha256")
ls -l dist
