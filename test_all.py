#!/usr/bin/env python3
"""Test all 6 MCP tools with a clean project"""

import subprocess, json, os, sys, time

workspace = "/home/thesamecat/.openclaw/workspace/mcp-sidecar/test-project"
binary = "./src/McpSidecar/bin/Release/net10.0/McpSidecar"

proc = subprocess.Popen(
    [binary],
    env={**os.environ, "MCP_WORKSPACE_ROOT": workspace},
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
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

# Wait for clangd to initialize
time.sleep(3)

print("=" * 60)
print("TESTING ALL 6 MCP TOOLS")
print("=" * 60)

# Test file and symbol: greet function at line 4
test_file = "main.cpp"
test_line = 4  # greet function
test_col = 10

# 1. symbol_resolve
print("\n1. symbol_resolve (query='greet')")
send({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_resolve","arguments":{"query":"greet","limit":5}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:400])

# 2. symbol_refs
print(f"\n2. symbol_refs ({test_file}:{test_line}:{test_col})")
send({"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbol_refs","arguments":{"file":test_file,"line":test_line,"column":test_col}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:400])

# 3. symbol_callers
print(f"\n3. symbol_callers ({test_file}:{test_line}:{test_col})")
send({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_callers","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":2}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:400])

# 4. symbol_callees
print(f"\n4. symbol_callees ({test_file}:{test_line}:{test_col})")
send({"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"symbol_callees","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":1}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:400])

# 5. symbol_card
print(f"\n5. symbol_card ({test_file}:{test_line}:{test_col})")
send({"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"symbol_card","arguments":{"file":test_file,"line":test_line,"column":test_col}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:600])

# 6. change_impact
print(f"\n6. change_impact ({test_file}:{test_line}:{test_col})")
send({"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"change_impact","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":2}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"][:400])

proc.terminate()
proc.wait()
print("\n" + "=" * 60)
print("TEST COMPLETE")
print("=" * 60)
