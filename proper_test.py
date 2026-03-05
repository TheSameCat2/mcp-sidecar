#!/usr/bin/env python3
import subprocess, json, os, sys, time, threading, select

env = dict(**os.environ)
env["MCP_WORKSPACE_ROOT"] = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
env["MCP_POSTGRES_CONNECTION"] = "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"

proc = subprocess.Popen(
    ["./src/McpSidecar/bin/Release/net10.0/McpSidecar"],
    env=env,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True
)

# Collect stderr in background
stderr_log = []
def read_stderr():
    for line in proc.stderr:
        stderr_log.append(line.strip())
threading.Thread(target=read_stderr, daemon=True).start()

def send(req):
    msg = json.dumps(req)
    proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
    proc.stdin.flush()

def recv(timeout=30):
    start = time.time()
    while time.time() - start < timeout:
        if proc.stdout in select.select([proc.stdout], [], [], 0.5)[0]:
            line = proc.stdout.readline()
            if line.startswith("Content-Length:"):
                length = int(line.split(":")[1].strip())
                proc.stdout.readline()
                body = ""
                remaining = length
                while remaining > 0:
                    chunk = proc.stdout.read(remaining)
                    if not chunk:
                        break
                    body += chunk
                    remaining -= len(chunk)
                return json.loads(body)
    return None

print("Waiting 15s for initialization...")
time.sleep(15)

print("\n=== Testing tools/list ===")
send({"jsonrpc":"2.0","id":1,"method":"tools/list"})
resp = recv()
if resp and "result" in resp:
    tools = resp["result"]["tools"]
    print(f"✅ Found {len(tools)} tools:")
    for t in tools:
        print(f"   - {t['name']}")
    print("\n✅ SUCCESS: MCP server is responding correctly!")
else:
    print(f"❌ Error: {resp}")
    print("\nStderr:")
    for line in stderr_log[-30:]:
        print(f"  {line}")

proc.terminate()
proc.wait()
