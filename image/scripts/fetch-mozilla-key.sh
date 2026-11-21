#!/bin/sh
# Downloads Mozilla's APT signing key, checks its fingerprint, and installs it where
# the image build (mkosi.sandbox/) and the finished image (mkosi.extra/) expect it.
set -eu
here=$(cd "$(dirname "$0")/.." && pwd)
expected=35BAA0B33E9EB396F59CA838C0BA5CE6DC6315A3
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT

curl -fsSL --proto '=https' https://packages.mozilla.org/apt/repo-signing-key.gpg -o "$tmp/key.asc"
actual=$(gpg --homedir "$tmp" -n -q --import --import-options import-show "$tmp/key.asc" \
    | awk '/^pub/ { getline; gsub(/^ +| +$/, ""); print; exit }')
if [ "$actual" != "$expected" ]; then
    echo "Mozilla key fingerprint mismatch: got '$actual', expected $expected" >&2
    exit 1
fi
for dir in mkosi.sandbox mkosi.extra; do
    install -D -m 0644 "$tmp/key.asc" "$here/$dir/etc/apt/keyrings/packages.mozilla.org.asc"
done
echo "Mozilla APT key $actual installed"
