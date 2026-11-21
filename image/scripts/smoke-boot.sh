#!/bin/sh
# Boots the x86-64 image headless in QEMU and proves the agent answers over vsock.
#   sudo scripts/smoke-boot.sh mkosi.output/ubit.raw ./ubit-hostsim inventory.json
# Uses KVM when available. Boots multi-user.target (no desktop) to keep it fast;
# the desktop is checked by hand on real hosts.
set -eu
image=${1:?image}; hostsim=${2:?ubit-hostsim binary}; inventory=${3:?inventory json}
work=$(mktemp -d)
log="$work/serial.log"
code=/usr/share/OVMF/OVMF_CODE_4M.fd
cp /usr/share/OVMF/OVMF_VARS_4M.fd "$work/vars.fd"

accel=tcg; [ -w /dev/kvm ] && accel=kvm
vsock=""
modprobe vhost_vsock 2>/dev/null || true
[ -w /dev/vhost-vsock ] && vsock="-device vhost-vsock-pci,guest-cid=3"

# shellcheck disable=SC2086
qemu-system-x86_64 -machine q35,accel=$accel -cpu max -m 4096 -smp 2 \
    -display none -serial "file:$log" \
    -drive if=pflash,format=raw,readonly=on,file=$code \
    -drive if=pflash,format=raw,file="$work/vars.fd" \
    -drive if=virtio,format=raw,file="$image",snapshot=on \
    -nic user,model=virtio-net-pci $vsock \
    -smbios "type=11,value=io.systemd.stub.kernel-cmdline-extra=console=ttyS0 systemd.unit=multi-user.target systemd.journald.forward_to_console=1" &
qemu=$!
trap 'kill $qemu 2>/dev/null || true; rm -rf "$work"' EXIT

echo "booting (accel=$accel)..."
i=0
until grep -q 'ubit-agent: listening on vsock:7710' "$log" 2>/dev/null; do
    i=$((i + 5))
    if [ "$i" -gt 900 ] || ! kill -0 $qemu 2>/dev/null; then
        echo "agent did not start; last serial output:" >&2
        tail -n 60 "$log" >&2 || true
        exit 1
    fi
    sleep 5
done
echo "agent is up after ~${i}s"

if [ -z "$vsock" ]; then
    echo "no /dev/vhost-vsock on this runner: skipping the vsock round trip"
    exit 0
fi
"$hostsim" --connect vsock:3:7710 --inventory "$inventory" --host-os windows --wait 60 | tee "$work/result.txt"
grep -q '^readiness score: [0-9]' "$work/result.txt"
echo "smoke test passed"
