#!/usr/bin/env python3
import subprocess, json, os, time, select

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

print("Waiting 12s for initialization...")
time.sleep(12)

# Test tools/list
req = {"jsonrpc":"2.0","id":1,"method":"tools/list"}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

# Read with timeout
for _ in range(30):
    if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
        line = proc.stdout.readline()
        if line.startswith("Content-Length:"):
            length = int(line.split(":")[1].strip())
            proc.stdout.readline()
            resp = json.loads(proc.stdout.read(length))
            tools = resp["result"]["tools"]
            print(f"\n✅ tools/list: Found {len(tools)} tools")
            for t in tools:
                print(f"   - {t['name']}")
            break
else:
    print("❌ Timeout waiting for tools/list")

# Test cpp.snapshot_status
req = {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"cpp.snapshot_status","arguments":{}}}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

for _ in range(30):
    if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
        line = proc.stdout.readline()
        if line.startswith("Content-Length:"):
            length = int(line.split(":")[1].strip())
            proc.stdout.readline()
            resp = json.loads(proc.stdout.read(length))
            if "result" in resp:
                text = resp["result"]["content"][0]["text"]
                print(f"\n✅ cpp.snapshot_status:\n{text[:500]}")
            else:
                print(f"\n❌ cpp.snapshot_status error: {resp}")
            break
else:
    print("❌ Timeout waiting for cpp.snapshot_status")

# Test cpp.build_explain
req = {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"cpp.build_explain","arguments":{"file":"Applications/AppLibraries/LogList/LogList/LogStack.cpp"}}}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

for _ in range(30):
    if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
        line = proc.stdout.readline()
        if line.startswith("Content-Length:"):
            length = int(line.split(":")[1].strip())
            proc.stdout.readline()
            resp = json.loads(proc.stdout.read(length))
            if "result" in resp:
                text = resp["result"]["content"][0]["text"]
                print(f"\n✅ cpp.build_explain:\n{text[:600]}")
            else:
                print(f"\n❌ cpp.build_explain error: {resp}")
            break
else:
    print("❌ Timeout waiting for cpp.build_explain")

proc.terminate()
proc.wait()
print("\n✅ Test complete")
