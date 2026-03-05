#!/usr/bin/env python3
"""Full test of all 6 MCP tools"""

import subprocess, json, os, sys, time

workspace = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
binary = "./src/McpSidecar/bin/Release/net10.0/McpSidecar"

proc = subprocess.Popen(
    [binary],
    env={**os.environ, "MCP_WORKSPACE_ROOT": workspace},
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True
)

def send_request(req):
    msg = json.dumps(req)
    proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
    proc.stdin.flush()

def read_response():
    line = proc.stdout.readline()
    while line and not line.startswith("Content-Length:"):
        line = proc.stdout.readline()
    if line.startswith("Content-Length:"):
        length = int(line.split(":")[1].strip())
        proc.stdout.readline()
        return json.loads(proc.stdout.read(length))
    return None

# Wait for startup
time.sleep(2)

# Test file and location: LogStack.cpp line 301 (clearLog)
test_file = "Applications/AppLibraries/LogList/LogList/LogStack.cpp"
test_line = 301
test_col = 20  # Somewhere in "clearLog"

print("=" * 60)
print("TESTING ALL 6 MCP TOOLS")
print("=" * 60)

# 1. symbol_resolve
print("\n1. symbol_resolve (query='clearLog')")
req = {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_resolve","arguments":{"query":"clearLog","limit":5}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:500] if text else "No results")
else:
    print("ERROR:", resp)

# 2. symbol_refs
print(f"\n2. symbol_refs ({test_file}:{test_line}:{test_col})")
req = {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"symbol_refs","arguments":{"file":test_file,"line":test_line,"column":test_col}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:500] if text else "No results")
else:
    print("ERROR:", resp)

# 3. symbol_callers
print(f"\n3. symbol_callers ({test_file}:{test_line}:{test_col})")
req = {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"symbol_callers","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":2}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:500] if text else "No results")
else:
    print("ERROR:", resp)

# 4. symbol_callees
print(f"\n4. symbol_callees ({test_file}:{test_line}:{test_col})")
req = {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"symbol_callees","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":1}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:500] if text else "No results")
else:
    print("ERROR:", resp)

# 5. symbol_card
print(f"\n5. symbol_card ({test_file}:{test_line}:{test_col})")
req = {"jsonrpc":"2.0","id":5,"method":"tools/call","params":{"name":"symbol_card","arguments":{"file":test_file,"line":test_line,"column":test_col}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:800] if text else "No results")
else:
    print("ERROR:", resp)

# 6. change_impact
print(f"\n6. change_impact ({test_file}:{test_line}:{test_col})")
req = {"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"change_impact","arguments":{"file":test_file,"line":test_line,"column":test_col,"depth":2}}}
send_request(req)
resp = read_response()
if resp and "result" in resp:
    text = resp["result"]["content"][0]["text"]
    print(text[:500] if text else "No results")
else:
    print("ERROR:", resp)

# Check stderr for any clangd errors
stderr = proc.stderr.read()
if stderr:
    print("\n" + "=" * 60)
    print("CLANGD LOGS:")
    print("=" * 60)
    print(stderr[:2000])

proc.terminate()
proc.wait()
print("\n" + "=" * 60)
print("TEST COMPLETE")
print("=" * 60)
