#!/usr/bin/env bash
set -euo pipefail

# CI/local regression gate for the standalone startup path.  It deliberately
# uses a disposable SQL Server container and a new database so migration history
# cannot hide an ordering or compile-time dependency.
container_name="slh-tms-fresh-bootstrap-sql"
sql_password="${SLH_FRESH_BOOTSTRAP_SQL_PASSWORD:-Slh!FreshBootstrap_${RANDOM}_${RANDOM}_Aa1}"
database_name="SLH_TMS_V2_BOOTSTRAP"
host_port="14334"
api_log="$(mktemp)"
api_pid=""

cleanup() {
  if [[ -n "$api_pid" ]]; then
    kill "$api_pid" 2>/dev/null || true
    wait "$api_pid" 2>/dev/null || true
  fi
  docker rm -f "$container_name" >/dev/null 2>&1 || true
  rm -f "$api_log"
}
trap cleanup EXIT

docker rm -f "$container_name" >/dev/null 2>&1 || true
docker run -d --name "$container_name" -p "$host_port:1433" \
  -e ACCEPT_EULA=Y -e MSSQL_PID=Express -e MSSQL_SA_PASSWORD="$sql_password" \
  mcr.microsoft.com/mssql/server:2022-latest >/dev/null

for _ in $(seq 1 60); do
  if docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -P "$sql_password" -C -Q 'SELECT 1' >/dev/null 2>&1; then
    break
  fi
  sleep 2
done

export ASPNETCORE_URLS="http://127.0.0.1:5087"
export ASPNETCORE_ENVIRONMENT=Production
export ConnectionStrings__TmsDb="Server=127.0.0.1,$host_port;Database=$database_name;User Id=sa;Password=$sql_password;Encrypt=True;TrustServerCertificate=True"
export Database__ExpectedDatabaseName="$database_name"
export Database__ApplySchemaChangesOnStartup=true
export Database__ApplyDeferredSchemaMigrations=false
export Entra__TenantId="${SLH_FRESH_BOOTSTRAP_ENTRA_TENANT_ID:-00000000-0000-0000-0000-000000000000}"
export Entra__Audience="${SLH_FRESH_BOOTSTRAP_ENTRA_AUDIENCE:-api://slh-tms-v2-bootstrap}"
export Entra__AllowedDomains__0="${SLH_FRESH_BOOTSTRAP_ALLOWED_DOMAIN:-lyonshaulage.com}"

for _ in $(seq 1 60); do
  if docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
      -S localhost -U sa -P "$sql_password" -C -Q 'SELECT 1' >/dev/null 2>&1; then
    break
  fi
  sleep 2
done
docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$sql_password" -C \
  -Q "IF DB_ID(N'$database_name') IS NULL CREATE DATABASE [$database_name];"

dotnet run --project Slh.Tms.Api.csproj --no-restore >"$api_log" 2>&1 &
api_pid=$!

healthy=false
for _ in $(seq 1 90); do
  if curl -fsS http://127.0.0.1:5087/api/v1/health | grep -qi 'healthy'; then
    healthy=true
    break
  fi
  if ! kill -0 "$api_pid" 2>/dev/null; then
    break
  fi
  sleep 2
done

if [[ "$healthy" != true ]]; then
  echo "Fresh-database API bootstrap failed. API log:" >&2
  sed -n '1,800p' "$api_log" >&2
  exit 1
fi

history_count="$(docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$sql_password" -C -d "$database_name" -h -1 -W \
  -Q 'SET NOCOUNT ON; SELECT COUNT(*) FROM dbo.SchemaMigration;' | tr -d '[:space:]')"
latest_version="$(docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$sql_password" -C -d "$database_name" -h -1 -W \
  -Q 'SET NOCOUNT ON; SELECT ISNULL(MAX(Version), 0) FROM dbo.SchemaMigration;' | tr -d '[:space:]')"
deferred_count="$(docker exec "$container_name" /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$sql_password" -C -d "$database_name" -h -1 -W \
  -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM (VALUES (47), (60)) AS deferred(Version) WHERE NOT EXISTS (SELECT 1 FROM dbo.SchemaMigration applied WHERE applied.Version = deferred.Version);" | tr -d '[:space:]')"
if [[ "$history_count" != "65" || "$latest_version" != "67" || "$deferred_count" != "2" ]]; then
  echo "Fresh database migration history is not contiguous: count=$history_count latest=$latest_version deferred_missing=$deferred_count" >&2
  sed -n '1,800p' "$api_log" >&2
  exit 1
fi

echo "Fresh SQL bootstrap passed: /api/v1/health healthy; $history_count required migration(s) applied through version $latest_version (042 and 060 remain explicitly deferred)."
