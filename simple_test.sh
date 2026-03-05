#!/bin/bash
set -e

export MCP_WORKSPACE_ROOT="/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
export MCP_POSTGRES_CONNECTION="Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"

BINARY="./src/McpSidecar/bin/Release/net10.0/McpSidecar"

# Start sidecar in background, capture PID
$BINARY 2>&1 | tee /tmp/sidecar.log &
SIDECAR_PID=$!

# Wait for initialization
sleep 15

# Send tools/list request
REQUEST='{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
echo "Content-Length: ${#REQUEST}"$'\r\r\n'"$REQUEST" | nc -q 1 localhost 2>/dev/null || echo "NC failed, trying direct pipe"

# Alternative: Use coproc
# coproc SIDECAR { $BINARY 2>&1; }
# sleep 15
# echo "Content-Length: ${#REQUEST}"$'\r\r\n'"$REQUEST" >&${SIDECAR[1]}

# Check logs
echo "=== Sidecar logs ==="
grep -E "(snapshot|started|listening|Error|error)" /tmp/sidecar.log | head -20

kill $SIDECAR_PID 2>/dev/null || true
