#!/bin/sh
# Downloads the latest ubit arm64 image from a GitHub release, checks its SHA-256,
# and decompresses it next to this script's package: ubit-mac-arm64.img
#   scripts/fetch-image.sh OWNER/REPO [TAG]
# Needs: gh (GitHub CLI, signed in) and zstd (brew install gh zstd).
set -eu
repo=${1:?usage: fetch-image.sh OWNER/REPO [TAG]}
tag=${2:-}
cd "$(dirname "$0")/.."
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

# shellcheck disable=SC2086
gh release download $tag --repo "$repo" --pattern 'ubit-mac-arm64-*.img.zst*' --dir "$tmp"
cd "$tmp"
shasum -a 256 -c ./*.sha256
zstd -d ./*.img.zst -o ubit-mac-arm64.img
cd - >/dev/null
mv "$tmp/ubit-mac-arm64.img" ./ubit-mac-arm64.img
echo "Image ready: $(pwd)/ubit-mac-arm64.img  (choose it in the app)"
