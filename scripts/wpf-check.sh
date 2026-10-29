#!/bin/bash
# Proves the VM can open a TCP connection to SQL Server on 32131 before anyone opens the app.
set -euo pipefail
host=${1:-dclab-vm}
cd "$(dirname "$0")/.."
set -a; . ./.env; set +a
addr=${MSSQL_BIND_VM:-192.168.122.1}
for port in 32131 32135; do
  ssh "$host" "powershell -NoProfile -Command \"(New-Object Net.Sockets.TcpClient).Connect('$addr',$port); 'tcp $port ok'\""
done
