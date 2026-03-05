PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS snapshot (
    snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
    repo_root TEXT NOT NULL,
    vcs_commit TEXT,
    workspace_hash TEXT NOT NULL,
    parent_snapshot_id INTEGER REFERENCES snapshot(snapshot_id) ON DELETE SET NULL,
    kind TEXT NOT NULL CHECK (kind IN ('background', 'overlay', 'imported')),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    last_updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    index_status TEXT NOT NULL DEFAULT 'in_progress' CHECK (index_status IN ('in_progress', 'complete', 'failed')),
    is_archived INTEGER NOT NULL DEFAULT 0 CHECK (is_archived IN (0, 1)),
    archived_at TEXT
);

CREATE INDEX IF NOT EXISTS ix_snapshot_repo_root_current ON snapshot(repo_root, is_archived, created_at DESC, snapshot_id DESC);
CREATE INDEX IF NOT EXISTS ix_snapshot_workspace_hash ON snapshot(workspace_hash);

CREATE TABLE IF NOT EXISTS file (
    file_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    path TEXT NOT NULL,
    real_path TEXT,
    content_hash TEXT,
    language TEXT NOT NULL CHECK (language IN ('c', 'c++', 'header', 'module_interface', 'module_impl')),
    is_generated INTEGER NOT NULL DEFAULT 0 CHECK (is_generated IN (0, 1)),
    is_external INTEGER NOT NULL DEFAULT 0 CHECK (is_external IN (0, 1)),
    size_bytes INTEGER NOT NULL DEFAULT 0,
    line_count INTEGER NOT NULL DEFAULT 0,
    UNIQUE (snapshot_id, path)
);

CREATE INDEX IF NOT EXISTS ix_file_snapshot_id ON file(snapshot_id);
CREATE INDEX IF NOT EXISTS ix_file_snapshot_real_path ON file(snapshot_id, real_path);

CREATE TABLE IF NOT EXISTS build_config (
    build_config_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    source_file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    output_path TEXT NOT NULL DEFAULT '',
    working_directory TEXT NOT NULL,
    argv_json TEXT NOT NULL DEFAULT '[]',
    argv_hash TEXT NOT NULL,
    compiler TEXT,
    language_standard TEXT,
    target_triple TEXT,
    sysroot TEXT,
    defines_hash TEXT,
    include_paths_hash TEXT,
    command_origin TEXT NOT NULL CHECK (command_origin IN ('exact', 'inferred', 'borrowed', 'imported')),
    command_text TEXT,
    UNIQUE (snapshot_id, source_file_id, argv_hash, output_path)
);

CREATE INDEX IF NOT EXISTS ix_build_config_snapshot_source_file_id ON build_config(snapshot_id, source_file_id);

CREATE TABLE IF NOT EXISTS parse_context (
    parse_context_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    build_config_id INTEGER NOT NULL REFERENCES build_config(build_config_id) ON DELETE CASCADE,
    context_kind TEXT NOT NULL CHECK (context_kind IN ('translation_unit', 'header_view', 'module_unit')),
    pp_fingerprint TEXT,
    borrowed_from_build_config_id INTEGER REFERENCES build_config(build_config_id) ON DELETE SET NULL,
    confidence REAL NOT NULL DEFAULT 1.0,
    parse_errors_json TEXT NOT NULL DEFAULT '[]'
);

CREATE INDEX IF NOT EXISTS ix_parse_context_snapshot_file_id ON parse_context(snapshot_id, file_id);
CREATE INDEX IF NOT EXISTS ix_parse_context_snapshot_build_config_id ON parse_context(snapshot_id, build_config_id);

CREATE TABLE IF NOT EXISTS provenance (
    provenance_id INTEGER PRIMARY KEY AUTOINCREMENT,
    extractor_name TEXT NOT NULL,
    extraction_method TEXT NOT NULL,
    exactness TEXT NOT NULL CHECK (exactness IN ('exact', 'approximate', 'inferred')),
    confidence REAL NOT NULL DEFAULT 1.0,
    evidence_json TEXT NOT NULL DEFAULT '{}'
);

CREATE TABLE IF NOT EXISTS symbol (
    symbol_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    stable_key TEXT NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    qualified_name TEXT NOT NULL,
    parent_symbol_id INTEGER REFERENCES symbol(symbol_id) ON DELETE SET NULL,
    visibility TEXT CHECK (visibility IN ('public', 'protected', 'private', 'internal', 'unknown')),
    template_kind TEXT NOT NULL DEFAULT 'non_template' CHECK (template_kind IN ('non_template', 'primary', 'partial_spec', 'full_spec', 'instantiation')),
    canonical_decl_id INTEGER,
    canonical_def_id INTEGER,
    is_exported INTEGER NOT NULL DEFAULT 0 CHECK (is_exported IN (0, 1)),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT,
    UNIQUE (snapshot_id, stable_key)
);

CREATE INDEX IF NOT EXISTS ix_symbol_snapshot_qualified_name ON symbol(snapshot_id, qualified_name);
CREATE INDEX IF NOT EXISTS ix_symbol_snapshot_parent_symbol_id ON symbol(snapshot_id, parent_symbol_id);

CREATE TABLE IF NOT EXISTS symbol_decl (
    decl_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    parse_context_id INTEGER REFERENCES parse_context(parse_context_id) ON DELETE SET NULL,
    role TEXT NOT NULL CHECK (role IN ('decl', 'def', 'fwd_decl')),
    span TEXT NOT NULL,
    signature_text TEXT,
    type_text TEXT,
    doc_comment TEXT,
    is_implicit INTEGER NOT NULL DEFAULT 0 CHECK (is_implicit IN (0, 1)),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_symbol_decl_snapshot_symbol_id ON symbol_decl(snapshot_id, symbol_id);
CREATE INDEX IF NOT EXISTS ix_symbol_decl_snapshot_file_id ON symbol_decl(snapshot_id, file_id);

CREATE TABLE IF NOT EXISTS relation (
    relation_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    parse_context_id INTEGER NOT NULL REFERENCES parse_context(parse_context_id) ON DELETE CASCADE,
    from_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    to_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('contains', 'inherits', 'overrides', 'specializes', 'instantiates', 'aliases', 'friend_of', 'constrained_by')),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT,
    UNIQUE (snapshot_id, parse_context_id, from_symbol_id, to_symbol_id, kind)
);

CREATE INDEX IF NOT EXISTS ix_relation_snapshot_from_symbol ON relation(snapshot_id, from_symbol_id);
CREATE INDEX IF NOT EXISTS ix_relation_snapshot_to_symbol ON relation(snapshot_id, to_symbol_id);

CREATE TABLE IF NOT EXISTS occurrence (
    occurrence_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    parse_context_id INTEGER NOT NULL REFERENCES parse_context(parse_context_id) ON DELETE CASCADE,
    file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    span TEXT NOT NULL,
    role_bits INTEGER NOT NULL DEFAULT 0,
    via_macro INTEGER NOT NULL DEFAULT 0 CHECK (via_macro IN (0, 1)),
    is_implicit INTEGER NOT NULL DEFAULT 0 CHECK (is_implicit IN (0, 1)),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_occurrence_snapshot_symbol ON occurrence(snapshot_id, symbol_id);
CREATE INDEX IF NOT EXISTS ix_occurrence_snapshot_file ON occurrence(snapshot_id, file_id);

CREATE TABLE IF NOT EXISTS type_edge (
    type_edge_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    owner_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    target_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    kind TEXT NOT NULL CHECK (kind IN ('return_type', 'param_type', 'field_type', 'base_type', 'alias_target', 'template_arg', 'constraint')),
    ordinal INTEGER NOT NULL DEFAULT 0,
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT,
    UNIQUE (snapshot_id, owner_symbol_id, target_symbol_id, kind, ordinal)
);

CREATE INDEX IF NOT EXISTS ix_type_edge_owner_symbol ON type_edge(snapshot_id, owner_symbol_id);

CREATE TABLE IF NOT EXISTS file_dependency (
    dep_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    parse_context_id INTEGER NOT NULL REFERENCES parse_context(parse_context_id) ON DELETE CASCADE,
    from_file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    to_file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    directive_kind TEXT NOT NULL CHECK (directive_kind IN ('include', 'import', 'module_import', 'header_unit')),
    literal_text TEXT,
    span TEXT NOT NULL,
    is_active INTEGER NOT NULL DEFAULT 1 CHECK (is_active IN (0, 1)),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_file_dependency_snapshot_from_file ON file_dependency(snapshot_id, from_file_id);
CREATE INDEX IF NOT EXISTS ix_file_dependency_snapshot_to_file ON file_dependency(snapshot_id, to_file_id);

CREATE TABLE IF NOT EXISTS callsite (
    callsite_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    parse_context_id INTEGER NOT NULL REFERENCES parse_context(parse_context_id) ON DELETE CASCADE,
    file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    caller_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    span TEXT NOT NULL,
    dispatch_kind TEXT NOT NULL CHECK (dispatch_kind IN ('direct', 'virtual', 'funcptr', 'ctor', 'dtor', 'operator', 'unresolved')),
    raw_text TEXT,
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT
);

CREATE INDEX IF NOT EXISTS ix_callsite_snapshot_caller_symbol ON callsite(snapshot_id, caller_symbol_id);

CREATE TABLE IF NOT EXISTS call_target (
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    callsite_id INTEGER NOT NULL REFERENCES callsite(callsite_id) ON DELETE CASCADE,
    callee_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    rank INTEGER NOT NULL DEFAULT 1,
    resolution_kind TEXT NOT NULL CHECK (resolution_kind IN ('direct', 'virtual_candidate', 'overload_candidate', 'funcptr_candidate', 'unknown')),
    confidence REAL NOT NULL DEFAULT 1.0,
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT,
    PRIMARY KEY (snapshot_id, callsite_id, callee_symbol_id, rank)
);

CREATE INDEX IF NOT EXISTS ix_call_target_snapshot_callee_symbol ON call_target(snapshot_id, callee_symbol_id);

CREATE TABLE IF NOT EXISTS macro_event (
    macro_event_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    parse_context_id INTEGER REFERENCES parse_context(parse_context_id) ON DELETE SET NULL,
    file_id INTEGER NOT NULL REFERENCES file(file_id) ON DELETE CASCADE,
    event_kind TEXT NOT NULL,
    macro_name TEXT,
    span TEXT,
    raw_text TEXT,
    provenance_id INTEGER REFERENCES provenance(provenance_id) ON DELETE SET NULL
);

CREATE TABLE IF NOT EXISTS callable_port (
    port_id INTEGER PRIMARY KEY AUTOINCREMENT,
    callable_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    port_kind TEXT NOT NULL CHECK (port_kind IN ('this', 'param', 'return', 'field', 'global', 'capture')),
    label TEXT NOT NULL,
    ordinal INTEGER NOT NULL DEFAULT 0,
    pointee_depth INTEGER NOT NULL DEFAULT 0,
    type_symbol_id INTEGER REFERENCES symbol(symbol_id) ON DELETE SET NULL,
    UNIQUE (callable_symbol_id, port_kind, ordinal, label)
);

CREATE TABLE IF NOT EXISTS flow_summary (
    flow_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    callable_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    from_port_id INTEGER NOT NULL REFERENCES callable_port(port_id) ON DELETE CASCADE,
    to_port_id INTEGER NOT NULL REFERENCES callable_port(port_id) ON DELETE CASCADE,
    flow_kind TEXT NOT NULL CHECK (flow_kind IN ('value', 'taint', 'alias', 'store', 'load', 'return', 'escape', 'ownership_transfer', 'mutates')),
    condition_kind TEXT NOT NULL CHECK (condition_kind IN ('always', 'may', 'nonnull', 'success_path', 'error_path')),
    engine TEXT NOT NULL CHECK (engine IN ('local_ast', 'codeql_ir', 'manual_model')),
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT
);

CREATE TABLE IF NOT EXISTS effect_summary (
    effect_id INTEGER PRIMARY KEY AUTOINCREMENT,
    snapshot_id INTEGER NOT NULL REFERENCES snapshot(snapshot_id) ON DELETE CASCADE,
    callable_symbol_id INTEGER NOT NULL REFERENCES symbol(symbol_id) ON DELETE CASCADE,
    effect_kind TEXT NOT NULL CHECK (effect_kind IN ('reads_global', 'writes_global', 'allocates', 'frees', 'locks', 'unlocks', 'throws', 'blocks', 'starts_thread', 'io')),
    target_symbol_id INTEGER REFERENCES symbol(symbol_id) ON DELETE SET NULL,
    provenance_id INTEGER NOT NULL REFERENCES provenance(provenance_id) ON DELETE RESTRICT,
    UNIQUE (snapshot_id, callable_symbol_id, effect_kind, target_symbol_id)
);

CREATE TABLE IF NOT EXISTS external_flow_model (
    model_id INTEGER PRIMARY KEY AUTOINCREMENT,
    library_name TEXT NOT NULL,
    symbol_matcher TEXT NOT NULL,
    from_port_spec TEXT,
    to_port_spec TEXT,
    model_kind TEXT NOT NULL CHECK (model_kind IN ('source', 'sink', 'summary')),
    origin TEXT NOT NULL CHECK (origin IN ('manual', 'imported_codeql_model'))
);

CREATE VIEW IF NOT EXISTS call_rollup_mv AS
SELECT
    cs.snapshot_id,
    cs.caller_symbol_id,
    ct.callee_symbol_id,
    COUNT(*) AS callsite_count,
    COUNT(*) AS candidate_count,
    COALESCE(MAX(ct.confidence), 0.0) AS max_confidence
FROM callsite cs
JOIN call_target ct
  ON ct.snapshot_id = cs.snapshot_id
 AND ct.callsite_id = cs.callsite_id
GROUP BY
    cs.snapshot_id,
    cs.caller_symbol_id,
    ct.callee_symbol_id;

CREATE VIEW IF NOT EXISTS symbol_card_mv AS
WITH decl_stats AS (
    SELECT
        sd.snapshot_id,
        sd.symbol_id,
        SUM(CASE WHEN sd.role = 'decl' THEN 1 ELSE 0 END) AS decl_count,
        SUM(CASE WHEN sd.role = 'def' THEN 1 ELSE 0 END) AS def_count,
        SUM(CASE WHEN sd.role = 'fwd_decl' THEN 1 ELSE 0 END) AS fwd_decl_count,
        COALESCE(
            MAX(CASE WHEN sd.role = 'def' AND sd.signature_text IS NOT NULL THEN sd.signature_text END),
            MAX(CASE WHEN sd.role = 'decl' AND sd.signature_text IS NOT NULL THEN sd.signature_text END),
            ''
        ) AS signature_text,
        MIN(sd.file_id) AS primary_file_id
    FROM symbol_decl sd
    GROUP BY sd.snapshot_id, sd.symbol_id
)
SELECT
    s.snapshot_id,
    s.symbol_id,
    s.kind,
    s.name,
    s.qualified_name,
    COALESCE(s.visibility, 'unknown') AS visibility,
    COALESCE(s.template_kind, 'non_template') AS template_kind,
    COALESCE(s.is_exported, 0) AS is_exported,
    s.parent_symbol_id AS owner_symbol_id,
    owner.qualified_name AS owner_qualified_name,
    COALESCE(ds.signature_text, '') AS signature_text,
    COALESCE(ds.decl_count, 0) AS decl_count,
    COALESCE(ds.def_count, 0) AS def_count,
    COALESCE(ds.fwd_decl_count, 0) AS fwd_decl_count,
    '[]' AS top_callers,
    '[]' AS top_callees,
    '[]' AS top_refs,
    '[]' AS key_effects,
    0 AS macro_hazard_count,
    0 AS macro_expand_count,
    ds.primary_file_id,
    COALESCE(f.real_path, f.path) AS primary_file_path
FROM symbol s
LEFT JOIN symbol owner
  ON owner.snapshot_id = s.snapshot_id
 AND owner.symbol_id = s.parent_symbol_id
LEFT JOIN decl_stats ds
  ON ds.snapshot_id = s.snapshot_id
 AND ds.symbol_id = s.symbol_id
LEFT JOIN file f
  ON f.snapshot_id = s.snapshot_id
 AND f.file_id = ds.primary_file_id;

CREATE VIEW IF NOT EXISTS file_overview_mv AS
WITH dep_stats AS (
    SELECT
        fd.snapshot_id,
        fd.from_file_id AS file_id,
        SUM(CASE WHEN fd.directive_kind = 'include' THEN 1 ELSE 0 END) AS include_count,
        SUM(CASE WHEN fd.directive_kind IN ('import', 'module_import', 'header_unit') THEN 1 ELSE 0 END) AS import_count
    FROM file_dependency fd
    WHERE fd.is_active = 1
    GROUP BY fd.snapshot_id, fd.from_file_id
),
def_stats AS (
    SELECT
        sd.snapshot_id,
        sd.file_id,
        COUNT(DISTINCT sd.symbol_id) AS defined_symbol_count
    FROM symbol_decl sd
    WHERE sd.role = 'def'
    GROUP BY sd.snapshot_id, sd.file_id
),
ref_stats AS (
    SELECT
        o.snapshot_id,
        o.file_id,
        COUNT(DISTINCT o.symbol_id) AS referenced_symbol_count
    FROM occurrence o
    WHERE o.is_implicit = 0
    GROUP BY o.snapshot_id, o.file_id
),
parse_stats AS (
    SELECT
        pc.snapshot_id,
        pc.file_id,
        COUNT(*) AS parse_context_count
    FROM parse_context pc
    GROUP BY pc.snapshot_id, pc.file_id
)
SELECT
    f.snapshot_id,
    f.file_id,
    f.path,
    f.real_path,
    f.language,
    f.size_bytes,
    f.line_count,
    COALESCE(dep.include_count, 0) AS include_count,
    COALESCE(dep.import_count, 0) AS import_count,
    COALESCE(defs.defined_symbol_count, 0) AS defined_symbol_count,
    COALESCE(refs.referenced_symbol_count, 0) AS referenced_symbol_count,
    '[]' AS top_external_dependencies,
    0.0 AS macro_density,
    CASE WHEN COALESCE(parse.parse_context_count, 0) > 0 THEN 1.0 ELSE 0.0 END AS parse_coverage
FROM file f
LEFT JOIN dep_stats dep
  ON dep.snapshot_id = f.snapshot_id
 AND dep.file_id = f.file_id
LEFT JOIN def_stats defs
  ON defs.snapshot_id = f.snapshot_id
 AND defs.file_id = f.file_id
LEFT JOIN ref_stats refs
  ON refs.snapshot_id = f.snapshot_id
 AND refs.file_id = f.file_id
LEFT JOIN parse_stats parse
  ON parse.snapshot_id = f.snapshot_id
 AND parse.file_id = f.file_id;
