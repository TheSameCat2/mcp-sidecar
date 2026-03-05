The best C++ design here is **not** “chunks in a vector DB.” It is a **build-aware fact graph** with three layers:

1. **Exact structural facts** from Clang/clangd.
2. **Precomputed local summaries** for calls, types, includes, and value movement.
3. **On-demand deep flow tracing** for the hard questions.

That shape matches the actual constraints of C++: parse correctness depends on compile commands, include paths, macros, target, and working directory; `compile_commands.json` can contain multiple command objects for the same source file; headers often do not have their own commands; clangd’s own whole-codebase index is built around `Symbol`, `Ref`, and `Relation`; and full global flow is possible but materially heavier than local flow. ([Clang][1])

## The storage model I’d actually use

**Canonical source of truth:** Postgres
**Search projection:** FTS index plus optional embeddings
**Heavy path engine:** CodeQL DBs on disk, queried on demand
**Live overlay:** an in-memory / fast local overlay for dirty files

I would **not** make a graph DB the source of truth. C++ indexing needs transactional incremental updates, exact file/range provenance, strong build-context keys, and lots of joins across files, symbols, and parse contexts. A relational core handles that better. Then you project the hot traversals into rollups and caches.

I would also mirror clangd’s layering idea: a durable background snapshot plus a fast overlay for live changes, then serve a merged view to the agent. clangd already does this with dynamic, background, static, and remote index layers because full-codebase indexing is expensive enough to justify caching and remote serving. ([Clangd][2])

## The schema

This is the schema I’d build first.

```text
snapshot
  snapshot_id
  repo_root
  vcs_commit
  workspace_hash
  parent_snapshot_id         -- nullable; for overlays/diffs
  kind                       -- background | overlay | imported
  created_at

file
  file_id
  snapshot_id
  path
  real_path
  content_hash
  language                   -- c | c++ | header | module_interface | module_impl
  is_generated
  is_external
  size_bytes
  line_count

build_config
  build_config_id
  snapshot_id
  source_file_id             -- main TU file
  output_path                -- optional; needed because same source can have multiple modes
  working_directory
  argv_json                  -- store argv, not just shell text
  argv_hash
  compiler
  language_standard
  target_triple
  sysroot
  defines_hash
  include_paths_hash
  command_origin             -- exact | inferred | borrowed | imported
  command_text               -- optional original string

parse_context
  parse_context_id
  snapshot_id
  file_id                    -- file actually parsed in this context
  build_config_id            -- originating TU context
  context_kind               -- translation_unit | header_view | module_unit
  pp_fingerprint             -- hash of active preprocessor state
  borrowed_from_build_config_id
  confidence                 -- 0..1
  parse_errors_json

provenance
  provenance_id
  extractor                  -- clang_ast | clangd_bg_index | clangd_dyn_index | codeql_ir | heuristic | manual_model
  method
  exactness                  -- exact | contextual | heuristic
  confidence                 -- 0..1
  evidence_json

symbol
  symbol_id
  snapshot_id
  stable_key                 -- canonical semantic identity, not file offsets
  kind                       -- namespace | class | struct | enum | function | method | field | var | typedef | alias | macro | ...
  name
  qualified_name
  parent_symbol_id
  visibility
  template_kind              -- non_template | primary | partial_spec | full_spec | instantiation
  canonical_decl_id
  canonical_def_id
  is_exported

symbol_decl
  decl_id
  symbol_id
  file_id
  parse_context_id           -- nullable for context-independent declarations
  role                       -- decl | def | fwd_decl
  span                       -- byte offsets + cached line/col
  signature_text
  type_text
  doc_comment
  is_implicit

occurrence
  occurrence_id
  snapshot_id
  parse_context_id
  file_id
  symbol_id
  span
  role_bits                  -- read | write | call | type_use | base_spec | override | addr_taken | macro_use | ...
  via_macro
  is_implicit
  provenance_id

relation
  relation_id
  snapshot_id
  parse_context_id
  from_symbol_id
  to_symbol_id
  kind                       -- contains | inherits | overrides | specializes | instantiates | aliases | friend_of | constrained_by
  provenance_id

type_edge
  type_edge_id
  snapshot_id
  owner_symbol_id
  target_symbol_id
  kind                       -- return_type | param_type | field_type | base_type | alias_target | template_arg | constraint
  ordinal
  provenance_id

file_dependency
  dep_id
  snapshot_id
  parse_context_id
  from_file_id
  to_file_id
  directive_kind             -- include | import | module_import | header_unit
  literal_text
  span
  is_active
  provenance_id

callsite
  callsite_id
  snapshot_id
  parse_context_id
  file_id
  caller_symbol_id
  span
  dispatch_kind              -- direct | virtual | funcptr | ctor | dtor | operator | unresolved
  raw_text
  provenance_id

call_target
  callsite_id
  callee_symbol_id
  rank
  resolution_kind            -- direct | virtual_candidate | overload_candidate | funcptr_candidate | unknown
  confidence

macro_event
  macro_event_id
  snapshot_id
  parse_context_id
  macro_symbol_id
  file_id
  span
  event_kind                 -- define | undef | expand
  expansion_text_hash
  provenance_id

callable_port
  port_id
  callable_symbol_id
  port_kind                  -- this | param | return | field | global | capture
  label                      -- this | p0 | p1 | return | field:state_
  ordinal
  pointee_depth              -- critical for C++
  type_symbol_id

flow_summary
  flow_id
  snapshot_id
  callable_symbol_id
  from_port_id
  to_port_id
  flow_kind                  -- value | taint | alias | store | load | return | escape | ownership_transfer | mutates
  condition_kind             -- always | may | nonnull | success_path | error_path
  engine                     -- local_ast | codeql_ir | manual_model
  provenance_id

effect_summary
  effect_id
  snapshot_id
  callable_symbol_id
  effect_kind                -- reads_global | writes_global | allocates | frees | locks | unlocks | throws | blocks | starts_thread | io
  target_symbol_id
  provenance_id

external_flow_model
  model_id
  library_name
  symbol_matcher
  from_port_spec
  to_port_spec
  model_kind                 -- source | sink | summary
  origin                     -- manual | imported_codeql_model

component
  component_id
  snapshot_id
  name
  kind                       -- directory | cmake_target | package | logical_module
  root_path

component_member
  component_id
  file_id
  symbol_id

component_dep
  from_component_id
  to_component_id
  kind                       -- include | call | type | link | ownership
  support_count

search_doc
  doc_id
  owner_kind                 -- symbol | file | component | flow
  owner_id
  title
  body
  keywords

embedding
  doc_id
  model
  vector
```

## Why this schema is the right one

### 1) `build_config` and `parse_context` are non-negotiable

For C++, parse context is part of the truth. Clang/clangd explicitly treat the compile command as the thing that configures the parser, and they call out include paths, language mode, predefined macros, target, compiler binary, and working directory as meaningful. The compilation database format also allows **multiple command objects for the same file**, and the `output` field exists specifically to distinguish different modes of the same input. Headers are a special problem because they often do not appear in `compile_commands.json` at all. ([Clangd][3])

That is why every context-sensitive fact in the schema hangs off `parse_context_id`, not just `file_id`.

### 2) `symbol / occurrence / relation` is the right core

clangd’s whole-codebase index already proves the basic trio: `Symbol`, `Ref`, and `Relation`. That is the correct base model. My schema keeps that, then adds **specialized tables** for the high-value C++ edges that agents actually care about: `callsite`, `call_target`, `file_dependency`, `type_edge`, and `macro_event`. ([Clangd][2])

I would **not** collapse everything into one generic `edge` table. Generic edges look elegant and perform badly. LLM-facing tools need fast, typed answers like “show callers,” “show include path,” “show param-to-return flow,” not “run a graph query.”

### 3) `callable_port` + `flow_summary` is the differentiator

This is the part most code intelligence systems skip, and it is exactly the thing you were reaching for.

Instead of only storing “A calls B,” you also store summaries like:

* `param:p0 -> return` as `return`
* `param:p0 -> field:buffer_` as `store`
* `field:buffer_ -> return` as `load`
* `this -> field:mutex_` as `mutates`
* `param:p1 -> global:registry` as `escape`

That makes it possible to answer, very quickly, questions like:

* “Does this function forward its input?”
* “Does it mutate object state?”
* “Does ownership transfer out?”
* “Which API returns data derived from this member?”

Then, for the truly hard cases, you run `trace_flow` on demand through CodeQL IR. That is the right split because CodeQL’s own docs say local flow is usually easier, faster, and more precise, while global flow is more powerful but costs more time and memory. The IR-based C++ dataflow library is also explicitly more semantically accurate than the AST-based one. ([CodeQL][4])

### 4) `external_flow_model` matters more than people think

Your repo is never the whole program. Third-party libraries, custom frameworks, and missing dependency code all break flow unless you model them. CodeQL supports exactly this with source/sink/summary models for library behavior outside the repo, so the schema should have a native place for those models. ([CodeQL][5])

### 5) Includes and macros deserve first-class treatment

C++ is not just a symbol graph. It is also a **preprocessor graph** and an **include graph**. clang-include-fixer already needs both a compilation database and a symbol index, and clangd’s include-cleaner reasons about direct file dependencies rather than only “does it compile.” That is why `file_dependency` and `macro_event` are first-class tables, not afterthoughts. ([Clang][6])

## The rollups and indexes I would add

Do not serve everything from raw fact tables. Add these materialized projections:

* `symbol_card_mv`

  * one row per symbol
  * signature, owner, decl/def, top callers, top callees, top refs, key effects, macro hazards

* `ref_rollup_mv`

  * dedupe the same ref across many parse contexts
  * keep `context_count`, `support_count`, and `max_confidence`

* `call_rollup_mv`

  * caller → callee aggregate with counts and representative callsites

* `impact_rollup_mv`

  * for each symbol/file: incoming refs, outgoing calls, dependent types, dependent headers, dependent components

* `file_overview_mv`

  * includes/imports, symbols defined, top external dependencies, macro density, parse coverage

Indexes:

* unique `build_config(snapshot_id, source_file_id, argv_hash, coalesce(output_path,''))`
* unique `symbol(snapshot_id, stable_key)`
* btree on `(occurrence.symbol_id, parse_context_id)`
* btree on `(callsite.caller_symbol_id)` and `(call_target.callee_symbol_id)`
* btree on `(file_dependency.from_file_id)` and `(file_dependency.to_file_id)`
* btree on `(flow_summary.callable_symbol_id, from_port_id, to_port_id)`
* partition `occurrence`, `callsite`, `call_target`, `macro_event` by `snapshot_id`
* FTS over `search_doc`
* vector index over `embedding` only if search quality actually benefits

**Do not precompute full transitive closure** of call graphs or data flow. Store 1-hop facts and cache common traversals.

## The MCP tools to implement

MCP tools should be **narrow, typed functions**. The protocol is built around named tools with schemas, while stable data objects fit naturally as URI-addressed resources. So the right design is: tools for actions/queries, resources for reusable outputs like symbol cards, traces, and context packs. ([Model Context Protocol][7])

Every tool should accept:

* `snapshot` — default: merged current view
* `build_scope` — `"merged" | "all" | build_config_id | [build_config_id]`
* `limit` / `page_token`
* `confidence_min`
* `include_evidence`

Every tool should return:

* stable IDs
* exact file spans
* provenance
* confidence
* truncation / pagination info
* warnings about missing coverage or heuristic contexts

### The core tools

1. **`cpp.snapshot_status`**
   Purpose: tell the agent whether the index is trustworthy here.
   Returns coverage, freshness, parse failures, borrowed-header counts, stale overlays.

2. **`cpp.build_explain`**
   Inputs: `file`, optional `line/column`.
   Returns the selected compile command, origin (`exact/inferred/borrowed`), key flags, include paths, macros, target, and parse warnings.
   This is one of the most important C++ tools.

3. **`cpp.search`**
   Inputs: free-text query, optional kind filters.
   Returns hybrid hits over symbols, files, components, and flow summaries.
   This is the only place embeddings really help.

4. **`cpp.resolve_symbol`**
   Inputs: name, qualified name, or source location.
   Returns candidate `symbol_id`s with score and disambiguation context.

5. **`cpp.symbol_card`**
   Inputs: `symbol_id`.
   Returns the compact reusable object for a symbol:

   * kind, signature, decl/def
   * owners / containing type / namespace
   * inheritance or specialization relations
   * top callers / callees
   * top refs
   * effects
   * macro / template hazards
   * related headers / components

6. **`cpp.file_overview`**
   Inputs: `file`.
   Returns defined symbols, referenced symbols, includes/imports, macros, parse contexts, and dominant dependencies.

7. **`cpp.references`**
   Inputs: `symbol_id`, optional role filters.
   Returns precise refs grouped by file or context, plus aggregate counts.

8. **`cpp.callers`**
   Inputs: `symbol_id`, `depth`, `include_virtual`, `include_funcptr`.
   Returns caller graph with evidence callsites.

9. **`cpp.callees`**
   Inputs: same shape as `callers`.
   Returns outgoing call graph with evidence.

10. **`cpp.dependency_path`**
    Inputs: `from`, `to`, optional edge kinds.
    Returns shortest / best explanatory path across include, call, type, inheritance, or component edges.

### The C++-specific tools that matter a lot

11. **`cpp.include_explain`**
    Inputs: `file`, optional unresolved identifier or `symbol_id`.
    Returns:

    * currently used headers
    * removable headers
    * missing headers
    * candidate headers for a symbol
    * rationale
      This is worth a dedicated tool because C++ include management is a real analysis problem, not just text editing. ([Clang][6])

12. **`cpp.preprocessor_hazards`**
    Inputs: `symbol_id` or `file`.
    Returns macro expansions, conditional-compilation forks, borrowed header contexts, and rename/impact hazards.

13. **`cpp.template_relations`**
    Inputs: `symbol_id`.
    Returns primary template, specializations, instantiations, constrained relations, and template-arg type edges.

### The flow and impact tools

14. **`cpp.flow_summary`**
    Inputs: `symbol_id`, mode = `value | taint | ownership`.
    Returns ports, summary edges, effects, and any external models involved.
    This is fast and should hit your precomputed summaries.

15. **`cpp.trace_flow`**
    Inputs: source spec, sink spec, mode, max paths, max hops.
    Returns exact paths with spans and edge explanations.
    This should choose the cheapest engine that can answer:

    * summary-only traversal first
    * escalate to local AST analysis
    * escalate to CodeQL IR path query only when needed

16. **`cpp.impact_analysis`**
    Inputs: targets = symbol(s) or file(s), change kind = `edit | rename | signature | delete`.
    Returns:

    * direct refs
    * transitive callers/callees
    * dependent types
    * dependent includes
    * affected components
    * likely risky macros / templates
    * confidence / risk score

### The refactor-preview tools

17. **`cpp.rename_preview`**
    Inputs: `symbol_id`, `new_name`, optional build scope.
    Returns:

    * candidate edits
    * blocked edits
    * macro hazards
    * incomplete coverage warnings
    * contexts where resolution is ambiguous
      I would ship **preview** before any apply tool.

18. **`cpp.safe_delete_preview`**
    Inputs: `symbol_id` or `file`.
    Returns remaining refs, weak/dynamic hazards, exported API risks, template instantiation risks, and generated-code risks.

19. **`cpp.context_pack`**
    Inputs: task description, target IDs, token budget.
    Returns a curated bundle of:

    * key symbol cards
    * file snippets
    * call paths
    * include paths
    * flow summaries
    * warnings
      This is the main bridge from your sidecar to the coding agent.

### One more tool I would definitely add

20. **`cpp.snapshot_diff`**
    Inputs: `before_snapshot`, `after_snapshot`, optional focus path/symbols.
    Returns:

    * added/removed/changed symbols
    * added/removed edges
    * changed flow summaries
    * changed include dependencies
      This is excellent for PR review and refactor validation.

## A few MCP resources I’d expose too

These are worth exposing as resources so the host can cache or pin them:

* `cpp://symbol/{symbol_id}`
* `cpp://file/{file_id}/overview`
* `cpp://trace/{trace_id}`
* `cpp://context-pack/{pack_id}`
* `cpp://snapshot/{snapshot_id}/status`

## What I would **not** implement early

* No raw `sql.query` tool
* No raw `graph.query` tool
* No eager whole-repo global data-flow precompute
* No apply-edit tools before previews are reliable
* No “vector search over file chunks” as the primary retrieval path

That stuff looks flexible and usually makes the agent worse.

## The shipping order

The fastest path to a useful system is:

1. `build_explain`
2. `resolve_symbol`
3. `symbol_card`
4. `references`
5. `callers` / `callees`
6. `file_overview`
7. `impact_analysis`
8. `context_pack`
9. `include_explain`
10. `flow_summary`
11. `trace_flow`
12. `rename_preview` / `safe_delete_preview`

That gives you a genuinely useful C++ sidecar before you burn months on full deep-flow ambition.

The next best move is to turn this into:

* concrete Postgres DDL,
* JSON Schemas for each MCP tool,
* and the extraction pipeline for `clangd/Clang + CodeQL`.

[1]: https://clang.llvm.org/docs/JSONCompilationDatabase.html "JSON Compilation Database Format Specification — Clang 23.0.0git documentation"
[2]: https://clangd.llvm.org/design/indexing "The clangd index"
[3]: https://clangd.llvm.org/design/compile-commands "Compile commands"
[4]: https://codeql.github.com/docs/codeql-language-guides/analyzing-data-flow-in-cpp/ "Analyzing data flow in C and C++ — CodeQL"
[5]: https://codeql.github.com/docs/codeql-language-guides/customizing-library-models-for-cpp/ "Customizing library models for C and C++ — CodeQL"
[6]: https://clang.llvm.org/extra/clang-include-fixer.html "Clang-Include-Fixer — Extra Clang Tools 23.0.0git documentation"
[7]: https://modelcontextprotocol.io/specification/2025-06-18/server/tools "Tools - Model Context Protocol"
