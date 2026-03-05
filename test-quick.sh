#!/bin/bash
# Quick test - run sidecar with LinearScanner, send MCP request via echo

WORKSPACE="/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"

# Build first
dotnet build -c Release src/McpSidecar 2>&1 | tail -3

# Test tools/list
REQUEST='{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
echo -e "Content-Length: ${#REQUEST}\r\r\n$REQUEST" | MCP_WORKSPACE_ROOT="$WORKSPACE" timeout 5 ./src/McpSidecar/bin/Release/net10.0/McpSidecar 2>&1 | head -30 || true

