#!/bin/sh
set -eu
umask 077
PATH=/usr/syno/bin:/usr/syno/sbin:/usr/local/bin:/usr/bin:/bin:/usr/sbin:/sbin
export PATH
test "$(id -u)" = 0
source_dir=/volume1/homes/boss/trade-http01-runtime-20260911
launch_base=/volume1/workplan-native-pilot-launch
test -d "$launch_base" && test ! -L "$launch_base"
test "$(readlink -f "$launch_base")" = "$launch_base"
test "$(stat -c %u "$launch_base")" = 0
test "$(stat -c %a "$launch_base")" = 700
run=$(mktemp -d "$launch_base/install-trade-XXXXXX")
test -f "$source_dir/renew-and-deploy.sh" && test ! -L "$source_dir/renew-and-deploy.sh"
cp "$source_dir/renew-and-deploy.sh" "$run/renew-and-deploy.sh"
chmod 600 "$run/renew-and-deploy.sh"
test "$(sha256sum "$run/renew-and-deploy.sh" | awk '{print $1}')" = 42d8dcbecf9bf8f8c47c67a801635c07a163a1d4e8cce8d9cd7a80ab6a681ac5
test -f "$source_dir/native_deploy.py" && test ! -L "$source_dir/native_deploy.py"
cp "$source_dir/native_deploy.py" "$run/native_deploy.py"
chmod 600 "$run/native_deploy.py"
test "$(sha256sum "$run/native_deploy.py" | awk '{print $1}')" = 92107f26756ab021532debdc3d63327670c647bc2b360969d1a1d827993f8051
test -f "$source_dir/native_api.py" && test ! -L "$source_dir/native_api.py"
cp "$source_dir/native_api.py" "$run/native_api.py"
chmod 600 "$run/native_api.py"
test "$(sha256sum "$run/native_api.py" | awk '{print $1}')" = c69df9498b67a084a2f74ed6992715446e95c781b586698bc5818f51ef340b71
test -f "$source_dir/transaction.py" && test ! -L "$source_dir/transaction.py"
cp "$source_dir/transaction.py" "$run/transaction.py"
chmod 600 "$run/transaction.py"
test "$(sha256sum "$run/transaction.py" | awk '{print $1}')" = bb44a250b80297a54493f4db4e1f4735a488826ace69233b6982988ed82010ce
test -f "$source_dir/install_support.py" && test ! -L "$source_dir/install_support.py"
cp "$source_dir/install_support.py" "$run/install_support.py"
chmod 600 "$run/install_support.py"
test "$(sha256sum "$run/install_support.py" | awk '{print $1}')" = 1a6050e6ac4f4da4a84d576ae6488d65829776064bbc392f7eea7c70ebe6a4d4
test -f "$source_dir/install_trade.py" && test ! -L "$source_dir/install_trade.py"
cp "$source_dir/install_trade.py" "$run/install_trade.py"
chmod 600 "$run/install_trade.py"
test "$(sha256sum "$run/install_trade.py" | awk '{print $1}')" = f9bf86a3ab6c95b53c3b62bae2b60e768ab4b0cca63e99a0e10fef6dcfedd243
test -f "$source_dir/payload-hashes.json" && test ! -L "$source_dir/payload-hashes.json"
cp "$source_dir/payload-hashes.json" "$run/payload-hashes.json"
chmod 600 "$run/payload-hashes.json"
test "$(sha256sum "$run/payload-hashes.json" | awk '{print $1}')" = 9adbea03fae4459f3e3f24f4b356e2e43837f6f5054c889dd61968bcdf1c1141
exec env -i PATH="$PATH" LC_ALL=C python3 -I -B "$run/install_trade.py" --install
