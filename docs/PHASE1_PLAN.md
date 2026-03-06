# Phase 1 Implementation Plan — Core Stability

**Goal:** Transform mcp-sidecar into a rock-solid single-binary deployment with reliable indexing and smart workspace detection.

**Timeline:** 4-6 weeks  
**Status:** Foundation exists (SQLite default, extraction working), needs hardening and polish.

---

## Current State Assessment

### ✅ Already Complete
- SQLite is the sole database backend (Postgres fully removed)
- Schema auto-creation on first run
- Basic extraction pipeline (clangd → SQLite)
- Core MCP tools working (symbol_resolve, refs, callers, etc.)
- compile_commands.json discovery

### ❌ Phase 1 Gaps
- Postgres code still in codebase (dependency bloat)
- No incremental indexing (full re-extract every time)
- No resume capability for interrupted extractions
- No stale compile_commands detection
- Parse failures not surfaced clearly
- Symbol identity validation across re-index runs
- Workspace detection is basic (env var or CWD)
- No multi-workspace support
- No single-binary packaging (requires .NET runtime)

---

## Detailed Implementation Plan

### Week 1-2: Single Binary Deployment

#### 1.1 ~~Remove Postgres Dependency~~ ✅ COMPLETE
**Completed 2026-03-06:** Postgres dependency fully removed from codebase.

**What was done:**
- Removed `Npgsql` package reference from `.csproj`
- Removed `PostgresDatabaseOptions` class
- Removed Postgres code path from `DbConnectionFactory`
- Removed Npgsql-specific overloads from `IDbConnectionExtensions`
- Updated `ExtractionService` to use SQLite-compatible parameter handling
- Cleaned up `appsettings.json` (removed Postgres config sections)
- Renamed misnamed "FromPostgres" methods to "FromDatabase"
- Changed `ResolutionSource` from "postgres" to "database"

**Validation:**
```bash
# Build succeeded, no Npgsql in deps.json
dotnet build
grep -i npgsql bin/Debug/net10.0/McpSidecar.deps.json  # Empty
```

#### 1.2 Ensure DB Auto-Creation is Bulletproof
**Files affected:** `DbConnectionFactory.cs`, `Schema/schema-sqlite.sql`

**Current issues:**
- Schema file discovery uses fragile path heuristics (won't work in single binary)
- No schema version tracking
- No migration path for schema changes

**Tasks:**
- [ ] Embed schema SQL as resource (no external file dependency)
- [ ] Add `schema_version` table with version number
- [ ] Add schema migration stub for future changes
- [ ] Test: Delete DB file, re-run, verify auto-creation
- [ ] Test: Corrupt DB file, verify error message is clear

**Schema versioning:**
```sql
CREATE TABLE schema_version (
  version INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL,
  description TEXT
);

-- Initial insert
INSERT INTO schema_version (version, applied_at, description)
VALUES (1, datetime('now'), 'Initial schema');
```

#### 1.3 Single Binary Packaging (CI/CD)
**Status:** Deferred to CI/CD setup

**Target platforms/architectures:**
- `linux-x64`, `linux-arm64`
- `osx-x64`, `osx-arm64`
- `win-x64` (stretch goal)

**CI/CD requirements:**
- [ ] GitHub Actions workflow for release builds
- [ ] Cross-platform testing matrix
- [ ] Build metadata injection (version, commit, build date)
- [ ] Artifact publishing (GitHub Releases)
- [ ] Strip debug symbols for release builds

**Prerequisite:** Schema must be embedded as resource (1.2) before single binary works correctly.

#### 1.4 Platform Portability Validation
**Status:** Manual testing for now, automate in CI/CD later

**Tasks:**
- [ ] Document clangd version requirements (>= 14)
- [ ] Test clangd detection on Linux and macOS
- [ ] Add platform-specific clangd path discovery

---

### Week 2-3: Indexing Reliability

#### 2.1 Incremental Indexing
**Current behavior:** Every `--extract` re-indexes all files from scratch

**Target behavior:** Only re-index files with changed content or compile commands

**Implementation approach:**
1. Track file content hash in `snapshot` or new `file_state` table
2. Track compile command hash per file
3. On extraction start, compute current hashes
4. Only process files with changed hashes
5. Preserve symbol data for unchanged files

**Schema changes:**
```sql
CREATE TABLE file_state (
  file_path TEXT PRIMARY KEY,
  content_hash TEXT NOT NULL,
  compile_command_hash TEXT NOT NULL,
  last_indexed_at TEXT NOT NULL,
  parse_success INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE compile_command (
  id INTEGER PRIMARY KEY,
  file_path TEXT NOT NULL,
  command_hash TEXT NOT NULL,
  command_json TEXT NOT NULL,
  UNIQUE(file_path, command_hash)
);
```

**Tasks:**
- [ ] Add file content hashing (SHA-256)
- [ ] Add compile command hashing
- [ ] Implement file change detection
- [ ] Modify extraction to skip unchanged files
- [ ] Add `--force` flag to override incremental behavior
- [ ] Test: Run extraction twice, verify second run is fast
- [ ] Test: Modify one file, verify only that file re-indexed

#### 2.2 Resume Interrupted Indexing
**Current behavior:** Ctrl+C kills process, all progress lost

**Target behavior:** Resume from last successfully indexed file

**Implementation approach:**
1. Track extraction progress in DB (`extraction_progress` table)
2. On interrupt, record last successfully processed file
3. On restart, query progress and continue from last position
4. Add `--clean` flag to start fresh

**Schema:**
```sql
CREATE TABLE extraction_progress (
  snapshot_id INTEGER PRIMARY KEY,
  status TEXT NOT NULL,  -- 'running', 'completed', 'interrupted', 'failed'
  started_at TEXT NOT NULL,
  completed_at TEXT,
  files_total INTEGER NOT NULL DEFAULT 0,
  files_processed INTEGER NOT NULL DEFAULT 0,
  files_failed INTEGER NOT NULL DEFAULT 0,
  last_file_processed TEXT,
  error_message TEXT,
  FOREIGN KEY (snapshot_id) REFERENCES snapshot(id)
);
```

**Tasks:**
- [ ] Create extraction progress tracking
- [ ] Implement progress persistence on interrupt (SIGINT handler)
- [ ] Add resume logic at extraction start
- [ ] Add progress reporting (file X/Y, percentage)
- [ ] Add `--clean-extraction` flag to ignore progress
- [ ] Test: Start extraction, Ctrl+C, resume, verify continuity
- [ ] Test: Kill -9 (hard kill), verify recovery behavior

#### 2.3 Detect Stale compile_commands.json
**Problem:** compile_commands.json may reference deleted files or be outdated

**Tasks:**
- [ ] Validate all files in compile_commands.json exist
- [ ] Warn if compile_commands.json is older than N days (configurable)
- [ ] Add `--validate-compile-commands` flag for explicit check
- [ ] Report missing files clearly (don't silently skip)
- [ ] Add `compile_commands_age_days` to extraction status output

**Validation output example:**
```
Validating compile_commands.json...
  Total entries: 1742
  Valid files: 1738
  Missing files: 4
    - src/deleted_file.cpp
    - old/moved_file.cpp
    ...
  
Warning: compile_commands.json is 47 days old.
Consider regenerating with: cmake -DCMAKE_EXPORT_COMPILE_COMMANDS=ON
```

#### 2.4 Detect Parse Failures and Report Clearly
**Current behavior:** Failed parses are logged but not aggregated

**Target behavior:** Clear summary at end of extraction with actionable advice

**Tasks:**
- [ ] Track parse failures in `file_state` table
- [ ] Aggregate failure reasons (syntax error, missing include, etc.)
- [ ] Report top failure categories at extraction end
- [ ] Add `--show-failures` flag to list all failed files
- [ ] Include failure count in MCP status response

**Example output:**
```
Extraction complete: snapshot_id=42
  Files: 1742 total, 1698 successful, 44 failed
  
Failure breakdown:
  - Missing includes: 23 files
  - Syntax errors: 12 files
  - Unknown: 9 files

Run with --show-failures for detailed list.
```

#### 2.5 Validate Symbol Identity Across Re-Index Runs
**Problem:** Symbol IDs may change between extractions, breaking historical queries

**Implementation approach:**
1. Generate stable symbol keys (file + name + signature hash)
2. Track symbol identity in `symbol` table
3. Detect and report symbol ID changes
4. Maintain symbol identity mapping for historical queries

**Tasks:**
- [ ] Implement stable symbol key generation
- [ ] Add symbol identity tracking
- [ ] Detect symbols that changed ID between snapshots
- [ ] Add warning when identity drift detected
- [ ] Consider: Add `symbol_identity` table to map stable keys to symbol IDs

---

### Week 3-4: Workspace Detection

#### 3.1 Auto-Detect Project Root
**Current behavior:** Relies on `MCP_WORKSPACE_ROOT` env var or CWD

**Target behavior:** Intelligent detection with fallback chain

**Detection strategy:**
1. Explicit: `--workspace` flag (highest priority)
2. Environment: `MCP_WORKSPACE_ROOT` env var
3. Detection: Walk up from CWD looking for:
   - `compile_commands.json`
   - `.git/` directory
   - `CMakeLists.txt`
   - `Makefile`
4. Fallback: Current working directory

**Tasks:**
- [ ] Implement workspace root detection algorithm
- [ ] Add `--workspace` CLI flag
- [ ] Add `--detect-workspace` flag to show detection result
- [ ] Test: Run from subdirectory, verify correct root detected
- [ ] Test: Run from non-project directory, verify error message

#### 3.2 Auto-Detect compile_commands.json
**Current behavior:** Hardcoded search paths

**Target behavior:** Smart search with multiple strategies

**Search strategies:**
1. Explicit: `--compile-commands` flag
2. Environment: `MCP_COMPILE_COMMANDS` env var
3. Workspace root: `<workspace>/compile_commands.json`
4. Build directory: `<workspace>/build/compile_commands.json`
5. Common locations: `<workspace>/{out,cmake-build-debug,cmake-build-release}/compile_commands.json`
6. Linked: Check for `compile_commands.json` symlink

**Tasks:**
- [ ] Implement search strategy chain
- [ ] Add `--compile-commands` CLI flag
- [ ] Add `--find-compile-commands` flag to show detection result
- [ ] Support multiple compile_commands.json files (merge strategy)
- [ ] Test: Various build directory structures

#### 3.3 Support Override Flags
**Tasks:**
- [ ] `--workspace <path>` — Explicit workspace root
- [ ] `--compile-commands <path>` — Explicit compile_commands.json
- [ ] `--database <path>` — Explicit SQLite database path
- [ ] `--clangd-path <path>` — Explicit clangd binary
- [ ] `--log-level <level>` — Logging verbosity
- [ ] Document all flags in `--help` output

#### 3.4 Multi-Workspace Indexing (Stretch Goal)
**Use case:** Monorepo with multiple compile_commands.json files

**Implementation approach:**
- Accept multiple `--compile-commands` flags
- Merge symbols into single database with workspace prefix
- Track source workspace in `symbol` table

**Tasks:**
- [ ] Design multi-workspace schema (add `workspace_id` column)
- [ ] Implement compile_commands.json merging
- [ ] Update queries to support workspace filtering
- [ ] Test: Index multiple projects into single DB

**Consideration:** May defer to Phase 2 if complexity is high

---

## Success Criteria

### Single Binary
- [ ] Binary runs on fresh Ubuntu VM without .NET installed
- [ ] Binary runs on macOS without .NET installed
- [ ] Binary size < 50MB (compressed)
- [ ] Startup time < 2 seconds

### Indexing Reliability
- [ ] Second extraction run is 90%+ faster than first (incremental)
- [ ] Interrupted extraction can be resumed
- [ ] Parse failures are clearly reported
- [ ] Stale compile_commands.json detected and warned

### Workspace Detection
- [ ] Workspace root detected correctly from subdirectory
- [ ] compile_commands.json found in common locations
- [ ] Override flags work as documented

---

## Risk Mitigation

### Risk: Incremental indexing breaks symbol consistency
**Mitigation:** Start with conservative incremental (only skip unchanged files with unchanged compile commands). Add `--force` for full re-index.

### Risk: Resume logic conflicts with incremental indexing
**Mitigation:** Resume only applies when previous extraction was interrupted. If extraction completed, incremental logic takes over.

### Risk: Workspace detection picks wrong directory
**Mitigation:** Always log detected workspace root. Add `--detect-workspace` to preview without action.

---

## Dependencies

### External
- clangd >= 14 (document minimum version)
- SQLite 3 (embedded, no external dependency)

### Internal
- .NET 10 SDK for builds
- CI/CD pipeline for release automation (future)

---

## Documentation Updates

After Phase 1 completion:
- [ ] Update README.md with single-binary installation instructions
- [ ] Document incremental indexing behavior
- [ ] Document workspace detection algorithm
- [ ] Add troubleshooting guide for common parse failures
- [ ] Update architecture diagram to remove Postgres

---

## Estimated Effort

| Component | Estimate | Status |
|-----------|----------|--------|
| ~~Remove Postgres~~ | ~~1-2 days~~ | ✅ Done |
| DB auto-creation hardening | 1 day | P0 |
| Single binary (CI/CD) | 2-3 days | Deferred to CI setup |
| Incremental indexing | 3-4 days | P0 |
| Resume interrupted indexing | 2-3 days | P1 |
| Stale compile_commands detection | 1 day | P1 |
| Parse failure reporting | 1-2 days | P1 |
| Symbol identity validation | 2 days | P2 |
| Workspace detection | 2-3 days | P0 |
| Multi-workspace support | 3-4 days | P2 (defer) |

**Total: 3-4 weeks remaining** (CI/CD packaging done separately)

---

## Next Actions

1. **~~Immediate:~~** ~~Remove Postgres dependency~~ ✅ DONE
2. **Week 1:** DB hardening + single binary packaging
3. **Week 2:** Incremental indexing implementation
4. **Week 3:** Workspace detection improvements
5. **Week 4:** Testing, documentation, release prep

---

*Plan created: 2026-03-06*  
*Synthia 💜🦇 — Let's build something solid, Same*
