#!/bin/sh
# Builds UbitMac, signs it ad hoc with the virtualization entitlement, and runs it.
set -eu
cd "$(dirname "$0")/.."
config=${CONFIG:-debug}
swift build -c "$config"
bin=".build/$config/UbitMac"
codesign --force --sign - --entitlements UbitMac.entitlements "$bin"
exec "$bin"
