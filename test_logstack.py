import subprocess, json, os, sys, time

workspace = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
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

# Wait for clangd to initialize and start indexing
print("Waiting for clangd to initialize (8s)...")
time.sleep(8)

# Test symbol_resolve for LogStack
print("\n=== Testing symbol_resolve for 'LogStack' ===")
send({"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"symbol_resolve","arguments":{"query":"LogStack","limit":5}}})
resp = recv()
if resp and "result" in resp:
    print(resp["result"]["content"][0]["text"])
else:
    print("Error:", json.dumps(resp, indent=2))

proc.terminate()
proc.wait()
