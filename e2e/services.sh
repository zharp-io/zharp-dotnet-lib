#!/usr/bin/env bash
# The databases the e2e apps use, on the zharp-e2e Docker network: Postgres (pg:5432) and,
# with `mssql`, SQL Server (mssql:1433). `e2e/services.sh down` removes them.
set -euo pipefail
export MSYS_NO_PATHCONV=1
if [ "${1:-}" = down ]; then docker rm -f zharp-e2e-pg zharp-e2e-mssql >/dev/null 2>&1 || true; exit 0; fi
docker network inspect zharp-e2e >/dev/null 2>&1 || docker network create zharp-e2e >/dev/null
if ! docker ps --format '{{.Names}}' | grep -q '^zharp-e2e-pg$'; then
  docker rm -f zharp-e2e-pg >/dev/null 2>&1 || true
  docker run -d --name zharp-e2e-pg --network zharp-e2e --network-alias pg -e POSTGRES_USER=e2e -e POSTGRES_PASSWORD=e2e -e POSTGRES_DB=shop postgres:16-alpine >/dev/null
fi
if [ "${1:-}" = mssql ] && ! docker ps --format '{{.Names}}' | grep -q '^zharp-e2e-mssql$'; then
  docker rm -f zharp-e2e-mssql >/dev/null 2>&1 || true
  docker run -d --name zharp-e2e-mssql --network zharp-e2e --network-alias mssql -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=E2e_Passw0rd!' mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi
for i in $(seq 1 60); do docker exec zharp-e2e-pg pg_isready -U e2e >/dev/null 2>&1 && break; sleep 1; done
echo "services up"
