#!/usr/bin/env python3
"""Final test of MCP Sidecar v1.1"""

import subprocess, json, os, sys, time

workspace = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
binary = "./src/McpSidecar/bin/Release/net10.0/McpSidecar"
conn_str = "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"

env = {
    **os.environ,
    "MCP_WORKSPACE_ROOT": workspace,
    "MCP_POSTGRES_CONNECTION": conn_str
}

proc = subprocess.Popen(
    [binary],
    env=env,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL,  # Ignore stderr for cleaner output
    text=True
)

def send(req):
    msg = json.dumps(req)
    proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
    proc.stdin.flush()

def recv():
    line = proc.stdout.readline()
    while line and not line.startswith("Content-Length:"):
        line = proc.stdout.readline()
    if line.startswith("Content-Length:"):
        length = int(line.split(":")[1].strip())
        proc.stdout.readline()
        return json.loads(proc.stdout.read(length))
    return None

print("Waiting for sidecar to initialize (8s)...")
time.sleep(8)

print("\n=== Testing tools/list ===")
send({"jsonrpc":"2.0","id":1,"method":"tools/list"})
resp = recv()
if resp and "result" in resp:
    tools = resp["result"]["tools"]
    print(f"Found {len(tools)} tools:")
    for t in tools:
        print(f"  - {t['name']}")

print("\n=== Testing cpp.snapshot_status ===")
send({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"cpp.snapshot_status","arguments":{}}})
resp = recv()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text)

print("\n=== Testing cpp.build_explain ===")
send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cpp.build_explain","arguments":{"file":"Applications/AppLibraries/LogList/LogList/LogStack.cpp"}}})
resp = recv()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:600])

proc.terminate()
proc.wait()
