#!/bin/bash
# Runs inside the mssql-init container. Idempotent.
set -euo pipefail
SQLCMD=(/opt/mssql-tools18/bin/sqlcmd -C -I -S mssql -U sa -P "$MSSQL_SA_PASSWORD" -b)

exists=$("${SQLCMD[@]}" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.databases WHERE name='AdventureWorks2022'" | tr -d '[:space:]')
if [ "$exists" = "0" ]; then
  echo "restoring AdventureWorks2022"
  "${SQLCMD[@]}" -Q "RESTORE DATABASE AdventureWorks2022 FROM DISK='/backup/AdventureWorks2022.bak'
    WITH MOVE 'AdventureWorks2022' TO '/var/opt/mssql/data/AdventureWorks2022.mdf',
         MOVE 'AdventureWorks2022_log' TO '/var/opt/mssql/data/AdventureWorks2022_log.ldf', STATS=20"
fi

"${SQLCMD[@]}" -d AdventureWorks2022 -Q "
IF OBJECT_ID('dbo.LabMigration') IS NULL
  CREATE TABLE dbo.LabMigration (Script nvarchar(200) NOT NULL PRIMARY KEY, AppliedAt datetime2 NOT NULL DEFAULT sysutcdatetime())"

for f in /sql/V*.sql; do
  [ -e "$f" ] || continue
  name=$(basename "$f")
  done_=$("${SQLCMD[@]}" -d AdventureWorks2022 -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.LabMigration WHERE Script='$name'" | tr -d '[:space:]')
  if [ "$done_" = "0" ]; then
    echo "applying $name"
    "${SQLCMD[@]}" -d AdventureWorks2022 -v LabAppPassword="$LAB_APP_PASSWORD" -i "$f"
    "${SQLCMD[@]}" -d AdventureWorks2022 -Q "INSERT dbo.LabMigration(Script) VALUES ('$name')"
  fi
done
echo "init done"
