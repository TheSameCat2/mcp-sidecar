#!/usr/bin/env python3
"""Actually test MCP tools return useful results"""

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
                return json.loads(proc.stdout.read(length))
    return None

print("Waiting for sidecar (12s)...")
time.sleep(12)

tests = [
    ("cpp.snapshot_status", {}),
    ("cpp.build_explain", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp"}),
    ("symbol_resolve", {"query": "LogStack", "limit": 5}),
    ("symbol_card", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp", "line": 301, "column": 20}),
]

results = {}
for i, (tool, args) in enumerate(tests, 1):
    print(f"\n{'='*60}")
    print(f"TEST {i}: {tool}")
    print(f"{'='*60}")
    
    send({"jsonrpc":"2.0","id":i,"method":"tools/call","params":{"name":tool,"arguments":args}})
    resp = recv(timeout=60)
    
    if not resp:
        print("❌ TIMEOUT")
        results[tool] = "TIMEOUT"
        continue
        
    if "error" in resp:
        print(f"❌ ERROR: {resp['error']}")
        results[tool] = "ERROR"
        continue
        
    if "result" not in resp:
        print(f"❌ NO RESULT")
        results[tool] = "NO_RESULT"
        continue
    
    text = resp["result"]["content"][0]["text"]
    
    # Check if result is useful (not just stub/empty)
    if len(text) < 50:
        print(f"⚠️  SHORT RESPONSE ({len(text)} chars)")
        results[tool] = "SHORT"
    elif "stub" in text.lower() or "not implemented" in text.lower():
        print(f"⚠️  STUB RESPONSE")
        results[tool] = "STUB"
    else:
        print(f"✅ USEFUL RESPONSE ({len(text)} chars)")
        results[tool] = "PASS"
    
    print(f"\n{text[:800]}")

print(f"\n{'='*60}")
print("SUMMARY")
print(f"{'='*60}")
for tool, status in results.items():
    emoji = "✅" if status == "PASS" else "❌"
    print(f"{emoji} {tool}: {status}")

proc.terminate()
proc.wait()
