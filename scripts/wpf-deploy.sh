#!/bin/bash
# Copies the built client to the VM and writes App.config's connection string from .env.
# Needs the VM's OpenSSH set up first (docs/todo/03-implementation.md -> Windows VM).
set -euo pipefail
host=${1:-dclab-vm}
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
out=legacy/wpf/WorkOrderDesk/bin/Release/net48
[ -f "$out/WorkOrderDesk.exe" ] || { echo "run make wpf-build first" >&2; exit 1; }
stage=$(mktemp -d); trap 'rm -rf "$stage"' EXIT
cp "$out"/*.exe "$out"/*.dll "$stage"/
sed "s|Password=CHANGE_ME|Password=${LAB_APP_PASSWORD}|; s|Server=[^;]*|Server=${MSSQL_BIND_VM:-192.168.122.1},32131|; s|http://192.168.122.1:32135|http://${MSSQL_BIND_VM:-192.168.122.1}:32135|" \
  "$out/WorkOrderDesk.exe.config" > "$stage/WorkOrderDesk.exe.config"
ssh "$host" 'if not exist C:\Lab\wpf mkdir C:\Lab\wpf'
scp "$stage"/* "$host":'C:/Lab/wpf/'
echo "deployed to $host:C:\\Lab\\wpf (start it from the VM desktop)"
