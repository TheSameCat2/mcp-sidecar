# MCP Sidecar 🐙⚙️💜

**C++ code intelligence for Claude, Cursor, and MCP clients.** clangd-powered symbol search, references, call hierarchy, build analysis, and data flow. **Zero config, SQLite-backed, single binary.**

## 🎯 What It Does

| Tool | What |
|------|------|
| `symbol_resolve` | Find symbols by name (`MainWindow`, `main`, `Scanner`) |
| `symbol_refs` | Find all uses of symbol at file:line:col |
| `symbol_callers` | Who calls this function? (incoming hierarchy) |
| `symbol_callees` | What does this function call? (outgoing hierarchy) |
| `cpp.build_explain` | Full compile command + flags for any file |
| `cpp.include_explain` | Used/required/removable includes |
| `symbol_card` | Symbol dashboard (decls, refs, callers, effects) |
| `change.impact` | What breaks if I change this symbol? |

## 🚀 Quick Start

```bash
# Install
dotnet tool install --global mcp-sidecar

# Pre-index (first time only, 5-15min for large projects)
mcp-sidecar --extract --workspace /path/to/cpp/project

# Use with Claude Desktop, Cursor, etc
mcp-sidecar --workspace /path/to/cpp/project
```

## 🧠 Features

- **Zero config** - Finds `compile_commands.json` automatically
- **SQLite** - No Postgres required, one file per workspace
- **Resume** - Interrupted extractions pick up where they left off
- **Incremental** - Re-indexes only changed files
- **10GB+ indexes** - Handles real C++ codebases (Qt, Boost, C++20)
- **Progress bar** - `mcp-sidecar --extract` shows live progress

## 📊 Example

```
$ mcp-sidecar --extract --workspace ~/LinearScanner-ICS
Extracting: ~/LinearScanner-ICS
Starting clangd... ✓
Starting extraction...

████████████████████████████ 100% | Files: 1742/1742 | Symbols: 35,206 | 12:47
✓ Extraction complete: snapshot_id=1
  Symbols: 35,206
  Files: 1,742
  Time: 12:47
```

Then in Claude:
```
> symbol_resolve MainWindow
Found 18 symbols matching 'MainWindow':
  MainWindow (class) @ Applications/Gui/Sources/MainWindow.h:42:8
  MainWindow::MainWindow (constructor) @ Applications/Gui/Sources/MainWindow.cpp:47:5
  ~MainWindow (destructor) @ Applications/Gui/Sources/MainWindow.cpp:57:5
```

## 🛠️ Architecture

```
MCP Client (Claude/Cursor) → MCP Sidecar → clangd (LSP)
                                     ↓
                               SQLite DB (symbols/refs/calls)
```

1. **clangd** - Real-time indexing via LSP
2. **SQLite** - Persistent symbol database
3. **MCP** - Standard protocol for AI tools

## 🎪 Why This Exists

Traditional C++ tools (ccls, clangd) are great for editors but terrible for AI. MCP Sidecar bridges the gap:

- **AI-ready responses** - Structured JSON with file:line:col locations
- **Build-aware** - Uses your exact compile_commands.json
- **Fast queries** - Pre-indexed for instant symbol search
- **No setup** - Just point it at a folder with compile_commands.json

## 🔧 Native Builds (Coming Soon)

```
# Single binary, no dotnet required
curl -L mcp-sidecar-linux-x64 -o mcp-sidecar
chmod +x mcp-sidecar
./mcp-sidecar --extract --workspace ~/project
```

## 📚 MCP Protocol

Full MCP spec: [mcp.so](https://mcp.so)

## 🙏 Thanks

Built with love by [Synthia](https://twitter.com/TheSameCat2) 💜

---

⭐ **Star on GitHub** | 🐙 **Issues welcome** | 💬 **Feedback appreciated**
