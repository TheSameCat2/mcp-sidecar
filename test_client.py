#!/usr/bin/env python3
"""
MCP Sidecar Test Client
Validates that tools return useful results, not just stubs
"""

import subprocess
import json
import os
import sys
import time
import threading
import queue

# Configuration
WORKSPACE = "/home/thesamecat/.openclaw/workspace/LinearScanner-ICS"
BINARY = "./src/McpSidecar/bin/Release/net10.0/McpSidecar"
CONN_STR = "Host=localhost;Port=5432;Database=mcp_sidecar;Username=mcp;Password=mcp_sidecar_2024"
INIT_TIMEOUT = 20  # seconds to wait for sidecar init
RESPONSE_TIMEOUT = 30  # seconds to wait for each response

class MCPClient:
    def __init__(self, binary, env):
        self.binary = binary
        self.env = env
        self.proc = None
        self.response_queue = queue.Queue()
        self.reader_thread = None
        self.stderr_log = []
        
    def start(self):
        """Start the sidecar process"""
        self.proc = subprocess.Popen(
            [self.binary],
            env=self.env,
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True
        )
        
        # Start reader thread
        self.reader_thread = threading.Thread(target=self._read_responses, daemon=True)
        self.reader_thread.start()
        
        # Start stderr reader thread
        self.stderr_thread = threading.Thread(target=self._read_stderr, daemon=True)
        self.stderr_thread.start()
        
    def _read_responses(self):
        """Background thread to read MCP responses"""
        try:
            while self.proc.poll() is None:
                line = self.proc.stdout.readline()
                if not line:
                    break
                    
                if line.startswith("Content-Length:"):
                    length = int(line.split(":")[1].strip())
                    self.proc.stdout.readline()  # blank line
                    
                    # Read exact length
                    body = ""
                    remaining = length
                    while remaining > 0:
                        chunk = self.proc.stdout.read(remaining)
                        if not chunk:
                            break
                        body += chunk
                        remaining -= len(chunk)
                    
                    try:
                        self.response_queue.put(json.loads(body))
                    except json.JSONDecodeError as e:
                        self.response_queue.put({"error": f"JSON decode error: {e}"})
        except Exception as e:
            self.response_queue.put({"error": f"Reader error: {e}"})
            
    def _read_stderr(self):
        """Background thread to collect stderr"""
        try:
            for line in self.proc.stderr:
                self.stderr_log.append(line.strip())
        except:
            pass
            
    def send(self, request):
        """Send an MCP request"""
        msg = json.dumps(request)
        self.proc.stdin.write(f"Content-Length: {len(msg)}\r\n\r\n{msg}")
        self.proc.stdin.flush()
        
    def recv(self, timeout=RESPONSE_TIMEOUT):
        """Receive an MCP response"""
        try:
            return self.response_queue.get(timeout=timeout)
        except queue.Empty:
            return {"error": "Timeout waiting for response"}
            
    def stop(self):
        """Stop the sidecar"""
        if self.proc:
            self.proc.terminate()
            try:
                self.proc.wait(timeout=5)
            except:
                self.proc.kill()
                
    def get_stderr(self):
        """Get collected stderr output"""
        return "\n".join(self.stderr_log)


def test_tool(client, tool_name, args, expected_keywords=None, min_length=100):
    """
    Test a single MCP tool
    Returns: (success, message, response_text)
    """
    print(f"\nTesting {tool_name}...")
    
    # Send request
    request_id = test_tool.counter
    test_tool.counter += 1
    
    client.send({
        "jsonrpc": "2.0",
        "id": request_id,
        "method": "tools/call",
        "params": {
            "name": tool_name,
            "arguments": args
        }
    })
    
    # Get response
    resp = client.recv()
    
    # Check for errors
    if "error" in resp:
        return False, f"Error: {resp['error']}", ""
    
    if "result" not in resp:
        return False, "No result in response", ""
    
    # Extract text
    try:
        text = resp["result"]["content"][0]["text"]
    except (KeyError, IndexError):
        return False, "Malformed response structure", ""
    
    # Validate response
    issues = []
    
    # Check length
    if len(text) < min_length:
        issues.append(f"Too short ({len(text)} chars, expected >= {min_length})")
    
    # Check for stub markers
    lower_text = text.lower()
    if "stub" in lower_text or "not implemented" in lower_text:
        issues.append("Contains stub markers")
    
    if "error:" in lower_text and "no " not in lower_text:
        issues.append("Contains error message")
    
    # Check for expected keywords
    if expected_keywords:
        missing = [kw for kw in expected_keywords if kw.lower() not in lower_text]
        if missing:
            issues.append(f"Missing keywords: {missing}")
    
    if issues:
        return False, "; ".join(issues), text
    else:
        return True, f"OK ({len(text)} chars)", text

test_tool.counter = 1


def main():
    print("=" * 70)
    print("MCP SIDECAR TEST CLIENT")
    print("=" * 70)
    
    env = {
        **os.environ,
        "MCP_WORKSPACE_ROOT": WORKSPACE,
        "MCP_POSTGRES_CONNECTION": CONN_STR
    }
    
    # Start client
    client = MCPClient(BINARY, env)
    print(f"\nStarting sidecar...")
    client.start()
    
    # Wait for initialization
    print(f"Waiting {INIT_TIMEOUT}s for initialization...")
    time.sleep(INIT_TIMEOUT)
    
    # Check if process is still alive
    if client.proc.poll() is not None:
        print(f"❌ Sidecar exited with code {client.proc.returncode}")
        print("\nStderr:")
        print(client.get_stderr())
        return 1
    
    print("✅ Sidecar is running")
    
    # Test 1: tools/list
    print("\n" + "=" * 70)
    print("TEST 1: tools/list")
    print("=" * 70)
    client.send({"jsonrpc": "2.0", "id": 0, "method": "tools/list"})
    resp = client.recv()
    
    if "error" in resp:
        print(f"❌ Error: {resp['error']}")
        client.stop()
        return 1
    
    if "result" not in resp:
        print(f"❌ No result")
        client.stop()
        return 1
    
    tools = resp["result"]["tools"]
    print(f"✅ Found {len(tools)} tools:")
    for t in tools:
        print(f"   - {t['name']}")
    
    # Test individual tools
    print("\n" + "=" * 70)
    print("TOOL VALIDATION TESTS")
    print("=" * 70)
    
    tests = [
        # (tool_name, args, expected_keywords, min_length)
        ("cpp.snapshot_status", {}, ["snapshot"], 100),
        ("cpp.build_explain", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp"}, ["compile", "command"], 200),
        ("symbol_resolve", {"query": "LogStack", "limit": 5}, ["symbol", "logstack"], 50),
        ("symbol_refs", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp", "line": 301, "column": 20}, None, 50),
        ("symbol_card", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp", "line": 301, "column": 20}, ["symbol"], 50),
        ("cpp.include_explain", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp"}, ["include"], 100),
        ("cpp.flow_summary", {"qualified_name": "LogStack::clearLog"}, None, 50),
        ("cpp.context_pack", {"file": "Applications/AppLibraries/LogList/LogList/LogStack.cpp", "token_budget": 2000}, None, 100),
    ]
    
    results = {}
    for tool_name, args, keywords, min_len in tests:
        success, message, text = test_tool(client, tool_name, args, keywords, min_len)
        results[tool_name] = success
        
        if success:
            print(f"✅ {tool_name}: {message}")
            # Show preview
            preview = text[:200].replace("\n", " ")
            print(f"   Preview: {preview}...")
        else:
            print(f"❌ {tool_name}: {message}")
            print(f"   Response: {text[:200]}")
    
    # Summary
    print("\n" + "=" * 70)
    print("SUMMARY")
    print("=" * 70)
    
    passed = sum(1 for v in results.values() if v)
    total = len(results)
    
    for tool, success in results.items():
        status = "✅ PASS" if success else "❌ FAIL"
        print(f"{status} {tool}")
    
    print(f"\nTotal: {passed}/{total} tests passed")
    
    # Show stderr if any errors
    if passed < total:
        print("\n" + "=" * 70)
        print("STDERR LOG (last 30 lines)")
        print("=" * 70)
        stderr = client.get_stderr()
        lines = stderr.split("\n")
        for line in lines[-30:]:
            if line:
                print(f"  {line}")
    
    # Always show stderr for debugging
    if not results:
        print("\n" + "=" * 70)
        print("STDERR LOG (first 30 lines)")
        print("=" * 70)
        stderr = client.get_stderr()
        lines = stderr.split("\n")
        for line in lines[:30]:
            if line:
                print(f"  {line}")
    
    client.stop()
    
    return 0 if passed == total else 1


if __name__ == "__main__":
    sys.exit(main())
