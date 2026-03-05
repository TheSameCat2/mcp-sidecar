What you’re picturing is real, and it’s a really productive direction: **a “code intelligence sidecar” that maintains a continuously-updated model of the repo**, and then exposes *task-shaped queries* to an agent (MCP tools, REST, whatever) so the agent isn’t constantly re-deriving relationships from scratch.

It’s basically: **structured code facts + derived graphs + (optional) embeddings**, served behind a fast query interface.

## Why this is “beyond LSP” in a useful way

LSP is great for *interactive editor operations* (definition, hover, references), but the deeper thing you described (“A is consumed by B, used for C, returned as D”) is closer to **program analysis**: call graphs, def-use chains, data-flow, taint-flow, dependency fanout, etc.

Also worth knowing: **LSIF** exists specifically to *dump / cache LSP-ish answers* so clients can serve LSP requests without a live language server. But LSIF deliberately does **not** try to define richer cross-cutting “symbol semantics” (overrides, etc.)—it’s basically “persisted LSP request results.” ([Microsoft GitHub][1])

That’s why your idea ends up being: “LSIF/SCIP-like index” **plus** “a richer fact layer.”

## You’re reinventing… some very good wheels (steal them)

A few existing systems are extremely aligned with your mental model:

* **SCIP (Sourcegraph)**: an indexing format used to power “go to definition”, “find references”, etc., and designed to be easier/faster than LSIF for large-scale indexing. ([Sourcegraph][2])
* **Glean (Meta)**: a centralized indexing service that stores “facts about code” and serves them via a query interface—explicitly motivated by “don’t make every IDE re-index the world.” ([Engineering at Meta][3])
* **CodeQL (GitHub)**: builds a database-like representation of a codebase, and those databases can include AST + control-flow + data-flow graphs, which is exactly the terrain you’re describing when you talk about “flows” and “used for / returned as.” ([CodeQL][4])
* **Code Property Graphs (Joern)**: another “unified graph representation” idea, explicitly merging classic representations (syntax, control-flow, data-flow) into one queryable graph. ([Joern Docs][5])
* **Tree-sitter**: a pragmatic way to get fast, incremental syntax trees (great for “cheap structure”), but it’s not a full semantic/type engine by itself. ([Tree-sitter][6])

And since you mentioned OpenClaw: what you learned there maps cleanly. OpenClaw’s memory system is “source-of-truth files + an index that’s updated by watching changes + hybrid retrieval (BM25 + vector).” That’s the same pattern you’d use for code—just with different extractors. ([OpenClaw][7])

## A practical architecture that actually works

Here’s the cleanest mental split:

### 1) “Facts” layer (deterministic, structured)

Store things you can point at and trust:

* **Symbols**: definition location, signature/type, docstring, visibility
* **Edges**:

  * `references` (who mentions symbol)
  * `calls` (call graph)
  * `imports/depends_on` (module/package dependencies)
  * `implements/overrides` (OO relationships)
  * `exports` / `public API surface`
* **Build context**: per language/project: tsconfig, go.mod, Cargo.toml, etc.

This is the layer you want for refactors and impact analysis because it’s *explainable* and *actionable*.

### 2) “Derived” layer (precomputed relationships)

This is where you start answering the questions agents actually ask:

* **Impact sets**: “if I change X, what fanout breaks?”
* **Slices**: “show me the minimal set of code involved in this request path”
* **Transitive callers/callees**: “who ultimately calls this?”
* **Hot paths / centrality**: “what’s the most-connected module here?”
* **Diff sketches**: “this PR added a call to foo(), removed method bar(), etc.” (Glean explicitly does this style of thing) ([Engineering at Meta][3])

You don’t necessarily store “all transitive closures” (those blow up). Instead, store enough to compute common queries quickly (depth-limited expansion, cached common traversals, etc.).

### 3) “Semantic search” layer (embeddings + lexical)

This is the “I don’t know the symbol name” layer.

* Embed:

  * function bodies (or summaries of them)
  * docstrings + comments
  * symbol “cards” (name + signature + top callers + top callees)
  * ADRs / design docs / READMEs

Use **hybrid retrieval** (BM25 + vectors) for better recall of identifiers + fuzzy intent (same reason OpenClaw does hybrid). ([OpenClaw][7])

### 4) “Tool-shaped API” layer (agent-friendly)

This is where MCP shines: you don’t give the agent a database, you give it *tools*.

Examples of MCP tools I’d expose (these are the “payoff” endpoints):

* `symbol.resolve(name_or_path, lang)` → canonical symbol ID(s)
* `symbol.card(symbol_id)` → definition + signature + doc + owners + “why it matters”
* `symbol.refs(symbol_id, filters…)`
* `symbol.callers(symbol_id, depth=1..N)`
* `symbol.callees(symbol_id, depth=1..N)`
* `module.deps(module_id, direction, depth)`
* `change.impact(files_or_symbols)` → ranked list of impacted symbols/tests
* `flow.trace(source, sink, kind)` → **data-flow/taint-flow path** (this is the “A→B→C→D” dream)
* `search.semantic(query, scope)` → returns symbol IDs + file/line ranges + short snippets
* `context.pack(task, target_symbol, budget_tokens)` → returns a curated, token-budgeted context bundle

The “context.pack” tool is surprisingly important: it turns a bunch of raw graph facts into *LLM-usable context* without dumping the whole repo.

## The part that people underestimate: keeping it fresh

Your idea of a “worker service maintaining understanding” lives or dies on incremental updates.

A good strategy:

* **Change detection**: git commits, file watcher, or CI event stream
* **Invalidate by dependency**:

  * dynamic languages: mostly “reindex changed files + local neighborhood”
  * compiled languages: handle “header/import fanout” (Glean calls this out as the hard part) ([Engineering at Meta][3])
* **Two-tier indexing**:

  * fast pass: parse/scan quickly (tree-sitter-level structure, cheap dependency edges) ([Tree-sitter][6])
  * slow pass: semantic index (compiler/LSP/CodeQL extraction) ([CodeQL][4])

This is exactly like OpenClaw’s approach: *files remain source of truth*, and the index is a derived cache that gets rebuilt when inputs/config change. ([OpenClaw][7])

## MVP path (so this doesn’t turn into a science project)

If you want this to be useful quickly, I’d build in this order:

1. **Definitions + references** (SCIP/LSIF-like)
   This alone enables: safe renames, “who uses this?”, dead-ish code detection. ([Microsoft GitHub][1])

2. **Module dependency graph**
   Gives you “system layout” and high-level map with almost no agent involvement.

3. **Call graph** (even an approximate one)
   Immediately useful for “what path leads to here?”

4. **Hybrid search** over “symbol cards” + docs
   Makes it usable when you don’t know names.

5. **Data-flow / taint-flow** (only for a couple of languages at first)
   This is the expensive “A→B→C→D” feature. CodeQL is a strong base here because it’s literally built around extracting a relational model and representing control/data flow. ([CodeQL][4])
   (Or go the CPG route if you like graph-native queries.) ([Joern Docs][5])

## The hard truths / gotchas (so you don’t get blindsided)

* **Perfect static data-flow is hard**, especially with dynamic dispatch, reflection, metaprogramming, JSON-as-API, etc. Your tool should be comfortable returning: “best-effort + confidence + provenance.”
* **Cross-language edges** are where this gets *really* valuable (e.g., protobuf → generated code → runtime usage), but also where you’ll need conventions/adapters.
* **Agents need “why” and “where,” not just facts**. Every answer should include file/line ranges and a short explanation of why that edge exists.
* **Access control matters** if this becomes centralized (multi-repo, multi-team). Your graph is basically “everything sensitive about the system.”

## Net: your idea is solid — and the “right shape”

If I had to summarize the sweet spot:

* Use **structured indexes** for truth (refs/calls/types/deps).
* Use **embeddings** for discovery and ranking, not as the source of truth.
* Expose it as **task-shaped MCP tools** so an agent can ask crisp questions and get crisp answers fast.

If you tell me what languages you care about (even just “mostly TypeScript + Go” or “Python monorepo”), I can give you a concrete “stack recipe” (indexers/extractors, storage schema, and the first 10 MCP tools that will make it feel magical).

[1]: https://microsoft.github.io/language-server-protocol/specifications/lsif/0.4.0/specification/ "LSIF Specification"
[2]: https://sourcegraph.com/blog/announcing-scip "SCIP - a better code indexing format than LSIF | Sourcegraph Blog"
[3]: https://engineering.fb.com/2024/12/19/developer-tools/glean-open-source-code-indexing/ "Indexing code at scale with Glean - Engineering at Meta"
[4]: https://codeql.github.com/docs/codeql-overview/about-codeql/ "About CodeQL — CodeQL"
[5]: https://docs.joern.io/code-property-graph/ "Code Property Graph | Joern Documentation"
[6]: https://tree-sitter.github.io/tree-sitter/ "Introduction - Tree-sitter"
[7]: https://docs.openclaw.ai/concepts/memory "Memory - OpenClaw"
