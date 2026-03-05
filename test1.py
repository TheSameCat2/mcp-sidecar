import subprocess, json, os, sys, time
proc = subprocess.Popen(
    ["./src/McpSidecar/bin/Release/net10.0/McpSidecar"],
    env={**os.environ, "MCP_WORKSPACE_ROOT": "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"},
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True
)
time.sleep(2)

# Test symbol_resolve
req = dict(jsonrpc="2.0", id=1, method="tools/call", params=dict(
    name="symbol_resolve", arguments=dict(query="clearLog", limit=5)))
msg = json.dumps(req)
proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
proc.stdin.flush()

line = proc.stdout.readline()
while line and not line.startswith("Content-Length:"):
    line = proc.stdout.readline()
if line.startswith("Content-Length:"):
    length = int(line.split(":")[1].strip())
    proc.stdout.readline()
    resp = json.loads(proc.stdout.read(length))
    print("symbol_resolve result:")
    print(resp["result"]["content"][0]["text"][:500])

proc.terminate()
proc.wait()
