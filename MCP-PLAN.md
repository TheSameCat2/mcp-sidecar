# MCP Code Intelligence Sidecar — Project Plan

## Overview

A C# MCP server that provides code intelligence tools for C++ codebases (v1), with Clang/clangd as the indexing backend. Future expansion to other languages.

## Architecture

```
┌─────────────────────────────────────────────────┐
│              MCP Client (Agent)                 │
└─────────────────────┬───────────────────────────┘
                      │ MCP protocol
┌─────────────────────▼───────────────────────────┐
│           C# MCP Server (Sidecar)               │
│  ┌─────────────┐  ┌──────────────────────────┐  │
│  │ Tool Layer  │  │ Graph/Fact Storage       │  │
│  │ (MCP tools) │  │ (SQLite or in-memory)    │  │
│  └──────┬──────┘  └──────────────────────────┘  │
└─────────┼───────────────────────────────────────┘
          │ LSP protocol
┌─────────▼───────────────────────────────────────┐
│              clangd subprocess                   │
│  - Reads compile_commands.json                  │
│  - Background indexing                          │
│  - Symbol/ref/call hierarchy queries            │
└─────────────────────────────────────────────────┘
```

## v1 Scope — C++ with Clangd

### Core Tools

| Tool | LSP Method | Status | Notes |
|------|------------|--------|-------|
| `symbol.resolve` | `workspace/symbol` | ✅ Implemented | Direct |
| `symbol.refs` | `textDocument/references` | ✅ Implemented | Direct |
| `symbol.callers` | `callHierarchy/incomingCalls` | ✅ Implemented | Direct |
| `symbol.callees` | `callHierarchy/outgoingCalls` | ✅ Implemented | Direct |
| `symbol.card` | Composed | ✅ Implemented | Definition + hover + refs + callers |
| `change.impact` | Derived | ✅ Implemented | Transitive closure over refs + callers |

### Features

- [x] Project scaffolding (Generic Host, basic structure)
- [x] Clangd lifecycle management (spawn, health check, restart)
- [x] `compile_commands.json` discovery/validation
- [x] LSP client implementation (JSON-RPC over stdio)
- [x] MCP server implementation (stdio transport)
- [x] Tool implementations for v1 scope (working, need real symbol testing)
- [x] Basic error handling + logging
- [x] Integration test against LinearScanner-ICS (partial - needs real symbol locations)

---

## v1.1 Scope — Build-Aware Fact Graph

**Goal:** Transform from live LSP queries to a persistent, build-aware fact storage with provenance tracking. Based on architectural vision from MCP3.md.

### Architecture Shift

```
┌─────────────────────────────────────────────────────┐
│                  MCP Client (Agent)                 │
└─────────────────────┬───────────────────────────────┘
                      │ MCP protocol
┌─────────────────────▼───────────────────────────────┐
│              C# MCP Server (Sidecar)                │
│  ┌──────────────┐  ┌────────────────────────────┐   │
│  │  Tool Layer  │  │  Postgres Fact Storage     │   │
│  │  (20 tools)  │  │  + Materialized Views      │   │
│  └──────┬───────┘  └────────────────────────────┘   │
│         │                    ▲                       │
│         │ LSP                 │ Extraction           │
│         ▼                     │                       │
│  ┌──────────────┐     ┌──────┴──────┐                │
│  │   clangd     │     │   CodeQL    │                │
│  │  (live + bg  │     │  (on-demand │                │
│  │   index)     │     │   deep)     │                │
│  └──────────────┘     └─────────────┘                │
└─────────────────────────────────────────────────────┘
```

### Core Schema (Postgres)

**Build Context Layer:**
- `snapshot` — versioned index state (background | overlay | imported)
- `build_config` — compile commands with argv_hash, target, sysroot, defines
- `parse_context` — file parsed in a specific build context, confidence score

**Symbol Layer:**
- `symbol` — stable_key, kind, qualified_name, visibility, template_kind
- `symbol_decl` — declarations/definitions with spans, signatures
- `occurrence` — refs with role_bits (read|write|call|type_use|override|...)
- `relation` — contains|inherits|overrides|specializes|instantiates|aliases

**C++ Specifics:**
- `file_dependency` — include/import graph with directive_kind
- `callsite` + `call_target` — call edges with dispatch_kind, resolution_kind
- `macro_event` — define|undef|expand tracking
- `type_edge` — return_type|param_type|field_type|base_type|template_arg

**Flow Layer (the differentiator):**
- `callable_port` — this|param|return|field with pointee_depth
- `flow_summary` — precomputed 1-hop value|taint|ownership|escape edges
- `effect_summary` — reads_global|writes_global|allocates|throws|io
- `external_flow_model` — library summaries for third-party code

**Provenance:**
- `provenance` — extractor, method, exactness, confidence for every fact

### New MCP Tools

| Tool | Purpose | Priority |
|------|---------|----------|
| `cpp.build_explain` | Show compile command, origin, flags, include paths for a file | **P0** |
| `cpp.snapshot_status` | Index coverage, freshness, parse failures | **P0** |
| `cpp.resolve_symbol` | Name/location → symbol_ids with disambiguation | P1 |
| `cpp.symbol_card` | Rich symbol dashboard (from materialized view) | P1 |
| `cpp.file_overview` | Defined symbols, includes, dependencies, macros | P1 |
| `cpp.references` | Precise refs with role filters, grouped by context | P1 |
| `cpp.callers` / `cpp.callees` | Call graph with evidence callsites | P1 |
| `cpp.include_explain` | Used/removable/missing headers, candidates | **P0** |
| `cpp.flow_summary` | Precomputed value/taint/ownership flows | **P0** |
| `cpp.trace_flow` | On-demand deep flow (escalates to CodeQL) | P2 |
| `cpp.impact_analysis` | Transitive refs + callers + types + includes | P1 |
| `cpp.context_pack` | Curated bundle for LLM agent (token budgeted) | **P0** |
| `cpp.rename_preview` | Candidate edits, blocked edits, macro hazards | P2 |
| `cpp.snapshot_diff` | Added/removed symbols + edges between snapshots | P2 |

### Materialized Views

- `symbol_card_mv` — one row per symbol, precomputed dashboard
- `ref_rollup_mv` — deduped refs across parse contexts
- `call_rollup_mv` — caller→callee aggregates with counts
- `impact_rollup_mv` — incoming refs, dependent types, dependent headers
- `file_overview_mv` — includes, symbols, dependencies, macro density

### v1.1 Milestones

- [ ] **M1: Postgres Schema** — DDL for core tables, indexes, materialized views
- [ ] **M2: Extraction Pipeline** — clangd LSP → Postgres ingestion
- [ ] **M3: build_explain** — Critical C++ tool, reveals parse context
- [ ] **M4: include_explain** — Include management, high value
- [ ] **M5: flow_summary** — Precomputed 1-hop flows (the differentiator)
- [ ] **M6: context_pack** — LLM agent bridge with token budgeting
- [ ] **M7: Provenance Layer** — Confidence scores, evidence tracking
- [ ] **M8: Snapshot/Versioning** — Incremental updates, diffs

### Key Principles (from MCP3.md)

1. **Build context is non-negotiable** — C++ parse results depend on compile commands
2. **Postgres > Graph DB** — Transactional updates, provenance, joins
3. **Typed tools, not query languages** — No raw SQL/graph tools for agents
4. **Precompute 1-hop, escalate for deep** — flow_summary fast, trace_flow on-demand
5. **No apply before preview** — rename_preview before any edit tools

---

### Deferred to v2+

| Tool | Why Deferred | Dependencies |
|------|--------------|--------------|
| `cpp.trace_flow` (deep) | Needs CodeQL infrastructure | CodeQL DB setup, query pipeline |
| `cpp.search.semantic` | Embeddings quality unclear | Vector DB, symbol embeddings |
| `cpp.safe_delete_preview` | Lower priority | Impact analysis + rename_preview |
| Template specialization tools | Complex, niche | Full template relation tracking |

## Technology Decisions

### C# Host: Generic Host ✅

Decision: Using `Microsoft.Extensions.Hosting` with stdio MCP transport.

### Project Structure

```
mcp-sidecar/
├── McpSidecar.sln
├── MCP.md              # Original design notes
├── MCP2.md             # Extended design notes
├── MCP-PLAN.md         # This file
├── .gitignore
└── src/
    └── McpSidecar/
        ├── McpSidecar.csproj
        ├── Program.cs              # Entry point, host setup
        ├── Models/
        │   └── LspTypes.cs         # LSP type definitions
        └── Services/
            ├── McpSidecarWorker.cs # IHostedService orchestrator
            ├── ClangdService.cs    # Clangd lifecycle + LSP client
            └── McpServer.cs        # MCP protocol + tool implementations
```

### Storage

- **v1:** In-memory graph, optional SQLite for persistence across restarts
- **v2+:** Consider dedicated graph DB if query complexity grows

### Clangd Integration

- Spawn as subprocess, communicate via stdio (LSP JSON-RPC)
- Wait for `workspace/executeCommand` clangd-specific "indexing complete" signal or poll `workspace/symbol`
- Cache results in C# layer to reduce LSP round-trips

## Open Questions

- [x] Which C++ repo to use as primary test target? → **LinearScanner-ICS**
- [x] MCP transport: stdio only, or support HTTP/SSE for remote access? → **stdio only for v1**
- [ ] How to handle multi-root workspaces (monorepos)

## Future Expansion

### v1.2 — C# Support (Roslyn)

Same MCP tool surface, Roslyn-backed implementation:
- Symbol resolution via `Compilation.GetSymbolsWithName`
- References via `SymbolFinder.FindReferencesAsync`
- Local data-flow via `AnalyzeDataFlow`
- Rename preview via `Renamer.RenameSymbolAsync`

### v1.3 — Deep Analysis

- CodeQL integration for full `trace_flow` support
- Custom Clang LibTooling passes for domain-specific relations
- Template specialization tracking
- Cross-language edges (protobuf → generated code → runtime)

### v2.0 — Scale & Remote

- Remote index server (clangd remote index protocol)
- Multi-repo support
- Shared index caching

---

## Next Steps

### v1 (Complete)
1. ✅ ~~Test ClangdService~~ — spawn clangd, verify LSP handshake works
2. ✅ ~~Implement tool logic~~ — replace stubs with actual LSP queries
3. ✅ ~~Add compile_commands.json discovery~~ — search parent directories
4. ✅ ~~Test with real C++ repo~~ — LinearScanner-ICS

### v1.1 (Ready for Codex Agent)
1. [ ] Create Postgres DDL for core schema
2. [ ] Build extraction pipeline (clangd → Postgres)
3. [ ] Implement `cpp.build_explain` tool
4. [ ] Implement `cpp.include_explain` tool
5. [ ] Implement `cpp.flow_summary` with precomputed edges
6. [ ] Implement `cpp.context_pack` with token budgeting
7. [ ] Add provenance/confidence tracking
8. [ ] Clean up v1 test files

---

## Reference Documents

- **MCP.md** — Original design notes
- **MCP2.md** — Extended tool ideas
- **MCP3.md** — Build-aware fact graph architecture (ChatGPT Pro)

---

*Last updated: 2026-03-04*
