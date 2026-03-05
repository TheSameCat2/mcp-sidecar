#!/usr/bin/env python3
import subprocess, json, os, sys, time
import threading

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
stderr_lines = []
def read_stderr():
    for line in proc.stderr:
        stderr_lines.append(line.strip())
threading.Thread(target=read_stderr, daemon=True).start()

print("Waiting 15s...")
time.sleep(15)

print("\nSending tools/list request...")
req = {"jsonrpc":"2.0","id":1,"method":"tools/list"}
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

print("Request sent, waiting for response...")
response_received = False
for i in range(30):
    line = proc.stdout.readline()
    if line.startswith("Content-Length:"):
        length = int(line.split(":")[1].strip())
        proc.stdout.readline()
        body = proc.stdout.read(length)
        resp = json.loads(body)
        if "result" in resp:
            tools = resp["result"]["tools"]
            print(f"Found {len(tools)} tools:")
            for t in tools:
                print(f"  - {t['name']}")
            response_received = True
        break
    elif line.strip():
        print(f"[STDOUT] {line.strip()}")
    
    if not response_received:
        print(f"Waiting... {i+1}/30")

if not response_received:
    print("\nNo response received")
    print("\nStderr output:")
    for line in stderr_lines[-20:]:
        print(f"  {line}")

proc.terminate()
proc.wait()
