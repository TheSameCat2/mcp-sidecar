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

print("Waiting 15s...")
time.sleep(15)

# Test cpp.snapshot_status
req = {"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"cpp.snapshot_status","arguments":{}}}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

print("Sent request, waiting for response...")
for _ in range(60):
    if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
        line = proc.stdout.readline()
        if line.startswith("Content-Length:"):
            length = int(line.split(":")[1].strip())
            proc.stdout.readline()
            resp = json.loads(proc.stdout.read(length))
            if "result" in resp:
                text = resp["result"]["content"][0]["text"]
                print(f"✅ SUCCESS ({len(text)} chars)")
                print(text)
            else:
                print(f"❌ ERROR: {resp}")
            break
else:
    print("❌ TIMEOUT after 60s")

proc.terminate()
proc.wait()
