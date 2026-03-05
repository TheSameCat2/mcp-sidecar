#!/usr/bin/env python3
"""Final test of MCP tools"""

import subprocess, json, os, time, sys

env = {
    **os.environ,
    "MCP_WORKSPACE_ROOT": "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS",
    "MCP_POSTGRES_CONNECTION": "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"
}

proc = subprocess.Popen(
    ["./src/McpSidecar/bin/Release/net10.0/McpSidecar"],
    env=env,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.DEVNULL,
    text=True
)

def send(req):
    msg = json.dumps(req)
    proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
    proc.stdin.flush()

def recv():
    # Read Content-Length header
    line = proc.stdout.readline()
    while line and not line.startswith("Content-Length:"):
        line = proc.stdout.readline()
    
    if not line.startswith("Content-Length:"):
        return None
    
    length = int(line.split(":")[1].strip())
    proc.stdout.readline()  # blank line
    
    # Read exact number of characters
    body = ""
    remaining = length
    while remaining > 0:
        chunk = proc.stdout.read(remaining)
        if not chunk:
            break
        body += chunk
        remaining -= len(chunk)
    
    return json.loads(body)

print("Waiting 10s for initialization...")
time.sleep(10)

# Test 1: tools/list
print("\n" + "="*60)
print("TEST 1: tools/list")
print("="*60)
send({"jsonrpc":"2.0","id":1,"method":"tools/list"})
resp = recv()
if resp and "result" in resp:
    tools = resp["result"]["tools"]
    print(f"✅ Found {len(tools)} tools:")
    for t in tools:
        print(f"   - {t['name']}")
else:
    print(f"❌ Error: {resp}")
    sys.exit(1)

# Test 2: cpp.snapshot_status
print("\n" + "="*60)
print("TEST 2: cpp.snapshot_status")
print("="*60)
send({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"cpp.snapshot_status","arguments":{}}})
resp = recv()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(f"✅ SUCCESS ({len(text)} chars)")
    print(text[:600])
else:
    print(f"❌ Error: {resp}")

# Test 3: cpp.build_explain
print("\n" + "="*60)
print("TEST 3: cpp.build_explain")
print("="*60)
send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cpp.build_explain","arguments":{"file":"Applications/AppLibraries/LogList/LogList/LogStack.cpp"}}})
resp = recv()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(f"✅ SUCCESS ({len(text)} chars)")
    print(text[:600])
else:
    print(f"❌ Error: {resp}")

# Test 4: symbol_resolve
print("\n" + "="*60)
print("TEST 4: symbol_resolve")
print("="*60)
send({"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"symbol_resolve","arguments":{"query":"LogStack","limit":5}}})
resp = recv()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(f"✅ SUCCESS ({len(text)} chars)")
    print(text[:600])
else:
    print(f"❌ Error: {resp}")

proc.terminate()
proc.wait()
print("\n" + "="*60)
print("ALL TESTS COMPLETE")
print("="*60)
