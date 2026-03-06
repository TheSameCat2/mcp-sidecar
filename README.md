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

## 🔧 Native Builds

✅ **Available now in v0.1.0!**

```bash
# Download from GitHub Releases
# Linux/macOS
tar -xzf mcp-sidecar-linux-x64.tar.gz
./mcp-sidecar --extract --workspace ~/project

# Windows
# Extract mcp-sidecar-win-x64.zip
mcp-sidecar.exe --extract --workspace C:\project
```

Platforms: `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`, `win-x64`

## 📚 MCP Protocol

Full MCP spec: [mcp.so](https://mcp.so)

## 🙏 Thanks

Made by Synthia with 💙

---

⭐ **Star on GitHub** | 🐙 **Issues welcome** | 💬 **Feedback appreciated**


---

# 🛣️ Development Roadmap

The goal of **mcp-sidecar** is not just symbol search, but a **build-aware fact engine that AI coding agents can trust when working in large C++ codebases**.

Below is the working roadmap. Items are intentionally checkable so automated agents (like OpenClaw / Synthia) can execute against them.

---

## Phase 1 — Core Stability ✅ COMPLETE (v0.1.0)

### Single Binary Deployment
- [x] Complete SQLite migration
- [x] Remove Postgres dependency entirely
- [x] Ensure DB auto‑creation on first run
- [x] Package as single binary build
- [x] Validate Linux / macOS / Windows portability

### Indexing Reliability
- [x] Robust incremental indexing
- [x] Resume interrupted indexing jobs
- [x] Detect stale compile_commands entries
- [x] Detect parse failures and report clearly
- [ ] Validate symbol identity across re‑index runs (Phase 2)

### Workspace Detection
- [x] Auto‑detect project root
- [x] Auto‑detect compile_commands.json
- [x] Support override flags
- [ ] Support multi‑workspace indexing (Phase 2)

---

## Phase 2 — Trust & Provenance

Goal: Every fact returned should explain **why it is trustworthy**.

### Deferred from Phase 1
- [ ] Validate symbol identity across re‑index runs
- [ ] Support multi‑workspace indexing

### Provenance Metadata
- [ ] Attach provenance metadata to results
- [ ] Mark origin: clangd / AST / graph / heuristic
- [ ] Return exact file:line:column locations
- [ ] Introduce confidence levels:
  - exact
  - high‑confidence
  - inferred
  - speculative

### Build Context Awareness

- [ ] Track compile flags per file
- [ ] Track macro environments
- [ ] Track platform/build variants
- [ ] Detect symbols hidden behind defines

### Parse Context Reporting

- [ ] Track files failing AST generation
- [ ] Report incomplete analysis conditions

---

## Phase 3 — Build Reality Modeling

Goal: reflect **the actual compilation environment**.

- [ ] Persist compile_commands in DB
- [ ] Model file → compile flag mapping
- [ ] Track include search paths

### Tools

- [ ] `cpp.build_explain`
- [ ] `cpp.include_explain`
- [ ] `cpp.macro_explain`

Example:

Symbol active because:
- BUILD_TARGET=Linux
- FEATURE_X enabled
- included via foo.hpp → bar.hpp

---

## Phase 4 — Change Impact Engine

Goal: allow agents to **safely modify code**.

- [ ] Build symbol dependency graph
- [ ] Track callers / callees
- [ ] Track type dependencies
- [ ] Track inheritance relationships

### Query

- [ ] `change.impact(symbol)`

Returns:

- directly affected symbols
- downstream call chains
- impacted files
- impacted modules

### Risk Ranking

- [ ] Rank breakage probability
- [ ] Highlight API boundaries
- [ ] Highlight template propagation

---

## Phase 5 — Flow & Effect Analysis (Key Differentiator)

Goal: understand **how data and effects propagate through the system**.

### Call Path Discovery

- [ ] Identify shortest call paths
- [ ] Identify high‑frequency call paths
- [ ] Detect likely entry points

### Flow Summaries

- [ ] `flow_summary(symbol)`

Example:

ButtonClick → UIHandler::StartScan → ScanController::Run → MotionPlanner::Execute

### Effect Summaries

- [ ] `effect_summary(symbol)`

Classify effects:

- IO
- hardware interaction
- memory mutation
- state transitions

---

## Phase 6 — Agent‑Optimized Output

Goal: return **minimal safe context for coding agents**.

### Context Packing

- [ ] `cpp.context_pack(symbol)`

Include:

- symbol definition
- key callers
- related types
- compile environment
- change risk factors

### Ranking

- [ ] Rank references by importance
- [ ] Rank files by change risk
- [ ] Rank call paths by likelihood

### Token Budget Awareness

- [ ] Configurable token limits
- [ ] Prefer highest‑value nodes

---

## Phase 7 — Performance & Scalability

Goal: support **large industrial C++ codebases**.

- [ ] Validate 100k+ symbol projects
- [ ] Validate 10k+ file repositories
- [ ] Track memory usage

### Index Performance

- [ ] Parallel indexing
- [ ] Lazy extraction
- [ ] Query caching

### DB Optimization

- [ ] SQLite query tuning
- [ ] Precomputed graph tables

---

## Phase 8 — Benchmarks & Validation

Goal: prove **agents perform better using mcp‑sidecar**.

### Developer Tasks

Benchmark:

- [ ] Trace behavior from UI event
- [ ] Trace hardware interaction path
- [ ] Safely rename a function
- [ ] Remove unused include
- [ ] Identify API boundaries

Measure:

- steps required
- tokens consumed
- correctness

### Agent Comparison

Compare:

Agent + grep  
Agent + embeddings  
Agent + mcp‑sidecar

---

## Phase 9 — Documentation & Examples

- [ ] Example refactor workflow
- [ ] Example debugging workflow
- [ ] Example API exploration

### Tutorials

- [ ] Setup guide
- [ ] MCP integration guide
- [ ] Tool usage examples

---

## Long‑Term Vision

**mcp‑sidecar becomes a build‑aware fact engine for codebases that AI agents can trust.**

Not just:

symbol search

But:

a system that explains how the software actually works.
