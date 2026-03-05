# MCP Sidecar v1.1 — Test & Launch Guide

## Test Credentials

**Postgres (Docker):**
```
Host: localhost
Port: 5432
Database: mcp_sidecar
Username: mcp
Password: mcp_sidecar_2024
```

**Connection String:**
```
Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024
```

---

## Quick Start

### 1. Start Postgres (Docker)

```bash
docker run -d \
  --name mcp-postgres \
  -e POSTGRES_USER=mcp \
  -e POSTGRES_PASSWORD=mcp_sidecar_2024 \
  -e POSTGRES_DB=mcp_sidecar \
  -p 5432:5432 \
  postgres:16
```

Wait for ready:
```bash
docker exec mcp-postgres pg_isready -U mcp -d mcp_sidecar
```

### 2. Initialize Database

```bash
cd ~/.openclaw/workspace/mcp-sidecar
cat src/McpSidecar/Schema/schema.sql | docker exec -i mcp-postgres psql -U mcp -d mcp_sidecar
```

Verify tables:
```bash
docker exec mcp-postgres psql -U mcp -d mcp_sidecar -c "\dt"
```

### 3. Build Sidecar

```bash
cd ~/.openclaw/workspace/mcp-sidecar
dotnet build -c Release
```

### 4. Run Sidecar

**Option A: appsettings.json (recommended)**

Ensure `src/McpSidecar/appsettings.json` contains:
```json
{
  "ConnectionStrings": {
    "Postgres": "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"
  }
}
```

Run:
```bash
cd ~/.openclaw/workspace/mcp-sidecar
MCP_WORKSPACE_ROOT=/path/to/your/cpp/project \
  ./src/McpSidecar/bin/Release/net10.0/McpSidecar
```

**Option B: Environment Variable**

```bash
cd ~/.openclaw/workspace/mcp-sidecar
MCP_WORKSPACE_ROOT=/path/to/your/cpp/project \
MCP_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024" \
  ./src/McpSidecar/bin/Release/net10.0/McpSidecar
```

### 5. Test MCP Tools

Send MCP request (requires Content-Length header):

```bash
# Test tools/list
REQUEST='{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
printf "Content-Length: %d\r\n\r\n%s" "${#REQUEST}" "$REQUEST" | \
  MCP_WORKSPACE_ROOT=/path/to/cpp/project \
  ./src/McpSidecar/bin/Release/net10.0/McpSidecar
```

**Using Python test script:**
```python
import subprocess, json, time

proc = subprocess.Popen(
    ["./src/McpSidecar/bin/Release/net10.0/McpSidecar"],
    env={
        **os.environ,
        "MCP_WORKSPACE_ROOT": "/path/to/cpp/project"
    },
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    text=True
)

time.sleep(3)  # Wait for clangd init

# Test cpp.snapshot_status
req = {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cpp.snapshot_status","arguments":{}}}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

# Read response
line = proc.stdout.readline()
while not line.startswith("Content-Length:"):
    line = proc.stdout.readline()
length = int(line.split(":")[1].strip())
proc.stdout.readline()  # blank line
response = json.loads(proc.stdout.read(length))
print(json.dumps(response, indent=2))
```

---

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `MCP_WORKSPACE_ROOT` | C++ project root directory | Current directory |
| `MCP_POSTGRES_CONNECTION` | Postgres connection string | From appsettings.json |
| `POSTGRES_CONNECTION_STRING` | Alternative connection string | From appsettings.json |

**Connection String Format:**
```
Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024
```

---

## Test Results (2026-03-04)

**Postgres Integration:** ✅ WORKING
- Snapshots created successfully
- Extraction pipeline running
- Clangd indexing 168 files from LinearScanner-ICS

**Tools Available:** 11 total
- v1: `symbol_resolve`, `symbol_refs`, `symbol_callers`, `symbol_callees`, `symbol_card`, `change_impact`
- v1.1: `cpp.build_explain`, `cpp.snapshot_status`, `cpp.include_explain`, `cpp.flow_summary`, `cpp.context_pack`

## Test Against LinearScanner-ICS

```bash
cd ~/.openclaw/workspace/mcp-sidecar

# Ensure LinearScanner's compile_commands.json paths are correct
sed -i 's|/home/thesamecat/dev/cpp/LinearScanner-ICS|/home/thesamecat/.openclaw/workspace/LinearScanner-ICS|g' \
  ~/.openclaw/workspace/LinearScanner-ICS/cmake-build-debug/compile_commands.json

# Run sidecar
MCP_WORKSPACE_ROOT=/home/thesamecat/.openclaw/workspace/LinearScanner-ICS \
  ./src/McpSidecar/bin/Release/net10.0/McpSidecar
```

---

## Cleanup

```bash
# Stop and remove Postgres container
docker stop mcp-postgres
docker rm mcp-postgres
```

---

## Troubleshooting

**"missing Postgres connection string"**
- Check appsettings.json exists and has correct connection string
- Or set MCP_POSTGRES_CONNECTION env var

**"Cannot create snapshot"**
- Ensure Postgres is running: `docker ps | grep mcp-postgres`
- Check connection: `docker exec mcp-postgres pg_isready -U mcp -d mcp_sidecar`

**"No symbols found"**
- Wait for clangd background indexing (can take minutes for large projects)
- Check compile_commands.json exists and has correct paths

**Clangd errors in logs**
- Ensure clangd is installed: `which clangd && clangd --version`
- Check compile_commands.json format is valid JSON

---

*Last updated: 2026-03-04*
