Yes — **C++ first is the right move**.

That’s where a sidecar indexer/worker is most differentiated, because the build context is harder, whole-repo indexing is more expensive, and the existing ecosystem already hints at the exact architecture you’re describing: clangd has a whole-codebase index with **symbols, refs, and relations**, caches index shards on disk, and even supports a **remote index** because large repos can take *hours* to index and need *multiple GB of RAM* when served locally. That is basically your “central worker service” idea in embryo. ([Clangd][1])

## How I’d build it for C++

I would **not** start with embeddings. I’d start with **Clang-backed facts**, then layer semantic search on top.

### 1) Ground truth: compile commands, not files

For C++, your worker should ingest `compile_commands.json` or `compile_flags.txt` and treat that as part of the source of truth. Clang’s compilation database format records the working directory, source file, and exact compile command for each translation unit, and clang tooling/LibTooling are built to consume it. clangd also uses it as the basis for indexing. ([Clang][2])

One ugly but important detail: **headers often do not have their own compile commands**. clangd handles that by borrowing a command from an includer or by filename-based heuristics, and the docs explicitly say that heuristic can be wrong sometimes. So in your own system, I’d attach a **confidence/provenance tag** to header-derived facts. ([Clangd][3])

### 2) Reuse clangd’s index layer instead of reinventing it

clangd’s index already models:

* **Symbols**: definitions/declarations/docs
* **Refs**: uses of a symbol in code
* **Relations**: symbol-to-symbol edges like inheritance

Its background index builds a complete project index from the compilation database and caches `*.idx` files to avoid reindexing unchanged code. That is a very solid base layer for definitions, references, and inheritance-ish structure. ([Clangd][1])

### 3) Add your own richer graph above that

Then add a custom worker pass with **LibTooling + ASTMatchers** for the things clangd does not directly hand you in the shape an agent wants:

* call edges
* return-value provenance
* “constructs / owns / stores / forwards” edges
* include graph
* public API surface
* test-to-code links
* allocator/resource-lifetime hints
* domain-specific relations

Clang’s docs are very explicit that LibTooling and ASTMatchers are meant for standalone query and source-to-source tools, which is exactly the bucket this belongs in. ([Clang][4])

### 4) Keep deep flow analysis as a second lane

For the “**A is consumed by B, used for C, returned as D**” class of query, I would not try to precompute everything eagerly.

Use two layers:

* **cheap derived graph** for common traversals
* **targeted deep flow engine** for hard questions

Clang’s data-flow docs describe the classic CFG-based fixpoint style of analysis, and CodeQL’s C/C++ libraries support both local and global data flow; its IR-based library explicitly says it gives a more precise semantic representation than the syntax-oriented AST-based approach. CodeQL path queries can also produce actual source-to-sink paths. ([Clang][5])

So my C++ recipe would be:

* **Base facts**: clangd/Clang index
* **Derived graph**: your worker
* **Heavy path tracing**: CodeQL or a custom Clang dataflow pass on demand

That gives you speed for everyday queries without pretending you can cheaply precompute every possible transitive flow.

## How I’d build it for C#

C# is the opposite story: **Roslyn already is the semantic substrate**.

Roslyn’s workspace model is specifically for **analysis and refactoring over entire solutions**, and it exposes projects/documents along with syntax trees, semantic models, and compilations. A `Workspace` has a current solution snapshot, and the solution model provides access to text, syntax trees, and semantics. ([Microsoft Learn][6])

### 1) Loader

For a standalone worker, I’d use the Roslyn MSBuild workspace stack. The official package `Microsoft.CodeAnalysis.Workspaces.MSBuild` is described as support for **analyzing MSBuild projects and solutions**, and `MSBuildLocator` exists to let custom applications use the public MSBuild APIs. ([NuGet][7])

### 2) Facts layer

Your C# worker can derive most of what you want directly from Roslyn:

* bind syntax nodes to symbols with `GetSymbolInfo`
* get whole-solution references with `SymbolFinder.FindReferencesAsync`
* use semantic models/compilations for type-aware analysis

Those APIs are first-class, official parts of Roslyn. ([Microsoft Learn][8])

### 3) Flow layer

Roslyn also gives you built-in **local** data-flow and control-flow analysis for parts of a method body via `AnalyzeDataFlow` and `AnalyzeControlFlow`. That won’t replace a full interprocedural engine, but it is perfect for building high-value facts like:

* read/write sets
* captured variables
* entry/exit points
* local value movement
* “this method mutates these members” style summaries ([Microsoft Learn][9])

### 4) Refactor execution

For actual refactor operations, Roslyn has `Renamer.RenameSymbolAsync`, which returns an updated `Solution`. So your agent-side workflow can be:

1. ask sidecar for impact/context
2. ask sidecar for rename preview
3. optionally apply changes

That is much cleaner than C++. ([Microsoft Learn][10])

### 5) Generated code matters

One more C# wrinkle: source generators are part of the picture. Microsoft’s docs note that source generators can read the compilation and add generated code, and Roslyn’s incremental generators are the high-performance replacement for v1 generators. So your indexer should explicitly decide whether generated artifacts are:

* fully indexed
* partially indexed
* tagged as generated and down-ranked in search ([Microsoft Learn][11])

## The architecture I’d actually choose

I’d build **one common graph model** with **two language adapters**.

Core entities:

* `Repo`
* `BuildUnit` (translation unit / project)
* `File`
* `Symbol`
* `Ref`
* `Relation`
* `CallEdge`
* `ContainmentEdge`
* `TypeEdge`
* `FlowSummary`
* `GeneratedArtifact`

Then:

* **C++ adapter** = Clang/clangd/CodeQL-backed
* **C# adapter** = Roslyn-backed

That lets your agent query one shape of data even though the ingestion is totally different.

## What I would not do

I would **not** make a vector database the center of this design.

Use vectors for:

* fuzzy symbol search
* “find code like this”
* doc ↔ code linking
* ranking candidate context packs

But the core should be a **typed fact graph**. For refactors and understanding, you want answers like:

* “this edge exists because this callsite bound to this symbol”
* “this file got this compile command from this TU”
* “this header fact is heuristic, confidence 0.62”

That is the difference between a tool you can trust and a tool that sounds smart.

## Rollout order

If I were doing this myself:

### Phase 1 — C++

* defs / refs / relations
* include graph
* callers / callees
* inheritance / override graph
* symbol cards
* hybrid search over symbol cards + docs

### Phase 2 — C#

Same API contract, but implemented with Roslyn:

* symbol resolution
* references
* local flow summaries
* rename preview / apply plan

### Phase 3 — selective deep tracing

* C++: CodeQL or custom Clang dataflow
* C#: CodeQL for cross-method / path-style tracing when Roslyn local flow is not enough ([CodeQL][12])

## My blunt take

**C++ is where this idea earns its keep.**
**C# is where you can make it feel polished fast.**

So the right sequence is:

1. prove value in C++ with hard navigation / impact / refactor support
2. port the same MCP/tool surface to C#
3. only then add embeddings as a ranking layer

Next useful step would be to sketch the **actual schema and MCP tools** for those two languages, not at the whiteboard level but as concrete entities, edges, and query signatures.

[1]: https://clangd.llvm.org/design/indexing "The clangd index"
[2]: https://clang.llvm.org/docs/JSONCompilationDatabase.html "JSON Compilation Database Format Specification — Clang 23.0.0git documentation"
[3]: https://clangd.llvm.org/design/compile-commands "Compile commands"
[4]: https://clang.llvm.org/docs/LibASTMatchersTutorial.html "Tutorial for building tools using LibTooling and LibASTMatchers — Clang 23.0.0git documentation"
[5]: https://clang.llvm.org/docs/DataFlowAnalysisIntro.html "Data flow analysis: an informal introduction — Clang 23.0.0git documentation"
[6]: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/work-with-workspace "Work with the .NET Compiler Platform SDK workspace model - C# | Microsoft Learn"
[7]: https://www.nuget.org/packages/Microsoft.CodeAnalysis.Workspaces.MSBuild/ "
        NuGet Gallery
        \| Microsoft.CodeAnalysis.Workspaces.MSBuild 5.0.0
    "
[8]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.modelextensions.getsymbolinfo?view=roslyn-dotnet-4.14.0 "ModelExtensions.GetSymbolInfo Method (Microsoft.CodeAnalysis) | Microsoft Learn"
[9]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.modelextensions.analyzedataflow?view=roslyn-dotnet-5.0.0 "ModelExtensions.AnalyzeDataFlow Method (Microsoft.CodeAnalysis) | Microsoft Learn"
[10]: https://learn.microsoft.com/en-us/dotnet/api/microsoft.codeanalysis.rename.renamer.renamesymbolasync?view=roslyn-dotnet-5.0.0 "Renamer.RenameSymbolAsync Method (Microsoft.CodeAnalysis.Rename) | Microsoft Learn"
[11]: https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/ "The .NET Compiler Platform SDK (Roslyn APIs) - C# | Microsoft Learn"
[12]: https://codeql.github.com/docs/codeql-language-guides/analyzing-data-flow-in-csharp/ "Analyzing data flow in C# — CodeQL"
