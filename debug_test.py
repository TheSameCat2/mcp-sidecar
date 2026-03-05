#!/usr/bin/env python3
import subprocess, json, os, time, select, sys

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
    stderr=subprocess.PIPE,
    text=True
)

print("Waiting 15s for initialization...", flush=True)
time.sleep(15)

# Check stderr
import threading
def read_stderr():
    for line in proc.stderr:
        if "MCP" in line or "snapshot" in line or "error" in line.lower():
            print(f"[STDERR] {line.strip()}", flush=True)

t = threading.Thread(target=read_stderr, daemon=True)
t.start()

# Test tools/list
req = {"jsonrpc":"2.0","id":1,"method":"tools/list"}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

print("\nSent tools/list request, waiting...", flush=True)

for i in range(30):
    if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
        line = proc.stdout.readline()
        print(f"[STDOUT] Got: {line[:80]}", flush=True)
        if line.startswith("Content-Length:"):
            length = int(line.split(":")[1].strip())
            proc.stdout.readline()
            resp = json.loads(proc.stdout.read(length))
            if "result" in resp:
                tools = resp["result"]["tools"]
                print(f"\n✅ Found {len(tools)} tools", flush=True)
                for t in tools:
                    print(f"  - {t['name']}", flush=True)
            break
    print(f"  Waiting... {i+1}/30", flush=True)
else:
    print("\n❌ TIMEOUT", flush=True)

proc.terminate()
proc.wait()
