#!/usr/bin/env python3
"""Comprehensive test suite for MCP Sidecar v1.1 against LinearScanner-ICS"""

import subprocess
import json
import os
import sys
import time

# Configuration
WORKSPACE = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
BINARY = "./src/McpSidecar/bin/Release/net10.0/McpSidecar"
CONN_STR = "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"

# Test file from LinearScanner
TEST_FILE = "Applications/AppLibraries/LogList/LogList/LogStack.cpp"
TEST_SYMBOL = "clearLog"

env = {
    **os.environ,
    "MCP_WORKSPACE_ROOT": WORKSPACE,
    "MCP_POSTGRES_CONNECTION": CONN_STR
}

print("=" * 70)
print("MCP SIDECAR v1.1 - COMPREHENSIVE TEST SUITE")
print("=" * 70)
print(f"Workspace: {WORKSPACE}")
print(f"Binary: {BINARY}")
print("=" * 70)

# Start sidecar
proc = subprocess.Popen(
    [BINARY],
    env=env,
    stdin=subprocess.PIPE,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    text=True
)

def send(req):
    msg = json.dumps(req)
    proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
    proc.stdin.flush()

def recv(timeout=30):
    import select
    start = time.time()
    while time.time() - start < timeout:
        if proc.stdout in select.select([proc.stdout], [], [], 1)[0]:
            line = proc.stdout.readline()
            if line.startswith("Content-Length:"):
                length = int(line.split(":")[1].strip())
                proc.stdout.readline()  # blank line
                return json.loads(proc.stdout.read(length))
    return None

def test_tool(name, args, expected_in_output=None):
    print(f"\n--- Testing {name} ---")
    send({"jsonrpc":"2.0","id":test_tool.counter,"method":"tools/call","params":{"name":name,"arguments":args}})
    test_tool.counter += 1
    resp = recv(timeout=60)
    if not resp:
        print(f"❌ TIMEOUT")
        return False
    if "error" in resp:
        print(f"❌ ERROR: {resp['error'].get('message', str(resp['error']))}")
        return False
    if "result" not in resp:
        print(f"❌ NO RESULT")
        return False
    
    text = resp["result"]["content"][0]["text"]
    print(f"✅ SUCCESS ({len(text)} chars)")
    if expected_in_output:
        if expected_in_output.lower() in text.lower():
            print(f"   ✓ Found expected: '{expected_in_output}'")
        else:
            print(f"   ⚠ Missing expected: '{expected_in_output}'")
    
    # Print first 400 chars
    preview = text[:400].replace('\n', '\n   ')
    print(f"   Preview:\n   {preview}...")
    return True

test_tool.counter = 1

# Wait for initialization
print("\n⏳ Waiting for sidecar initialization (10s)...")
time.sleep(10)

# Read any stderr to check status
import select
if proc.stderr in select.select([proc.stderr], [], [], 0)[0]:
    err_output = proc.stderr.read()
    if "Created snapshot" in err_output:
        print("✅ Postgres snapshot created")
    if "error" in err_output.lower():
        print(f"⚠ Stderr has errors")

# Test 1: tools/list
print("\n" + "=" * 70)
print("TEST 1: tools/list")
print("=" * 70)
send({"jsonrpc":"2.0","id":0,"method":"tools/list"})
resp = recv()
if resp and "result" in resp:
    tools = resp["result"]["tools"]
    print(f"✅ Found {len(tools)} tools:")
    for t in tools:
        print(f"   - {t['name']}")
    test_results = {"tools_list": True}
else:
    print(f"❌ Failed: {resp}")
    test_results = {"tools_list": False}

# Test v1.1 tools
print("\n" + "=" * 70)
print("TEST 2-6: v1.1 Postgres-backed tools")
print("=" * 70)

test_results["cpp_snapshot_status"] = test_tool("cpp.snapshot_status", {}, "snapshot")
test_results["cpp_build_explain"] = test_tool("cpp.build_explain", {"file": TEST_FILE}, "compile")
test_results["cpp_include_explain"] = test_tool("cpp.include_explain", {"file": TEST_FILE}, "include")

# Test v1 LSP-backed tools
print("\n" + "=" * 70)
print("TEST 7-11: v1 LSP-backed tools")
print("=" * 70)

test_results["symbol_resolve"] = test_tool("symbol_resolve", {"query": TEST_SYMBOL, "limit": 5})
test_results["symbol_refs"] = test_tool("symbol_refs", {"file": TEST_FILE, "line": 301, "column": 20})
test_results["symbol_callers"] = test_tool("symbol_callers", {"file": TEST_FILE, "line": 301, "column": 20, "depth": 1})
test_results["symbol_callees"] = test_tool("symbol_callees", {"file": TEST_FILE, "line": 301, "column": 20, "depth": 1})
test_results["symbol_card"] = test_tool("symbol_card", {"file": TEST_FILE, "line": 301, "column": 20})

# Test change_impact
print("\n" + "=" * 70)
print("TEST 12: change_impact")
print("=" * 70)
test_results["change_impact"] = test_tool("change_impact", {"file": TEST_FILE, "line": 301, "column": 20, "depth": 2})

# Test cpp.flow_summary and cpp.context_pack
print("\n" + "=" * 70)
print("TEST 13-14: Advanced v1.1 tools")
print("=" * 70)

test_results["cpp_flow_summary"] = test_tool("cpp.flow_summary", {"qualified_name": "LogStack::clearLog"})
test_results["cpp_context_pack"] = test_tool("cpp.context_pack", {"file": TEST_FILE, "token_budget": 4000})

# Summary
print("\n" + "=" * 70)
print("TEST SUMMARY")
print("=" * 70)

passed = sum(1 for v in test_results.values() if v)
failed = sum(1 for v in test_results.values() if not v)

for name, result in test_results.items():
    status = "✅ PASS" if result else "❌ FAIL"
    print(f"   {name}: {status}")

print(f"\nTotal: {passed} passed, {failed} failed out of {len(test_results)} tests")

if failed == 0:
    print("\n🎉 ALL TESTS PASSED!")
else:
    print(f"\n⚠️ {failed} test(s) failed")

# Cleanup
proc.terminate()
proc.wait()

sys.exit(0 if failed == 0 else 1)
