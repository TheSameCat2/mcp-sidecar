-- MCP Code Intelligence Sidecar v1.1
-- Milestone M1: Postgres schema for build context, symbols, C++ specifics, flow, and provenance.

BEGIN;

-- =========================
-- Build Context Layer
-- =========================

CREATE TABLE snapshot (
    snapshot_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    repo_root TEXT NOT NULL,
    vcs_commit TEXT,
    workspace_hash TEXT NOT NULL,
    parent_snapshot_id BIGINT,
    kind TEXT NOT NULL CHECK (kind IN ('background', 'overlay', 'imported')),
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    last_updated_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    index_status TEXT NOT NULL DEFAULT 'in_progress'
        CHECK (index_status IN ('in_progress', 'complete', 'failed')),
    is_archived BOOLEAN NOT NULL DEFAULT FALSE,
    archived_at TIMESTAMPTZ,
    CONSTRAINT ck_snapshot_archive_consistency
        CHECK ((is_archived = FALSE AND archived_at IS NULL) OR is_archived = TRUE),
    CONSTRAINT fk_snapshot_parent
        FOREIGN KEY (parent_snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE SET NULL
);

COMMENT ON TABLE snapshot IS
    'Versioned index state for a repository (background, overlay, or imported snapshot).';

CREATE INDEX ix_snapshot_parent_snapshot_id ON snapshot (parent_snapshot_id);
CREATE INDEX ix_snapshot_repo_root_current ON snapshot (repo_root, is_archived, created_at DESC, snapshot_id DESC);
CREATE INDEX ix_snapshot_workspace_hash ON snapshot (workspace_hash);

CREATE TABLE file (
    file_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    path TEXT NOT NULL,
    real_path TEXT,
    content_hash TEXT,
    language TEXT NOT NULL CHECK (language IN ('c', 'c++', 'header', 'module_interface', 'module_impl')),
    is_generated BOOLEAN NOT NULL DEFAULT FALSE,
    is_external BOOLEAN NOT NULL DEFAULT FALSE,
    size_bytes BIGINT NOT NULL DEFAULT 0 CHECK (size_bytes >= 0),
    line_count INTEGER NOT NULL DEFAULT 0 CHECK (line_count >= 0),
    CONSTRAINT fk_file_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT uq_file_snapshot_file_id
        UNIQUE (snapshot_id, file_id),
    CONSTRAINT uq_file_snapshot_path
        UNIQUE (snapshot_id, path)
);

COMMENT ON TABLE file IS
    'Snapshot-scoped file catalog used by parse contexts, symbol locations, and dependency edges.';

CREATE INDEX ix_file_snapshot_id ON file (snapshot_id);
CREATE INDEX ix_file_snapshot_real_path ON file (snapshot_id, real_path);

CREATE TABLE build_config (
    build_config_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    source_file_id BIGINT NOT NULL,
    output_path TEXT,
    working_directory TEXT NOT NULL,
    argv_json JSONB NOT NULL,
    argv_hash TEXT NOT NULL,
    compiler TEXT,
    language_standard TEXT,
    target_triple TEXT,
    sysroot TEXT,
    defines_hash TEXT,
    include_paths_hash TEXT,
    command_origin TEXT NOT NULL CHECK (command_origin IN ('exact', 'inferred', 'borrowed', 'imported')),
    command_text TEXT,
    CONSTRAINT fk_build_config_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_build_config_source_file
        FOREIGN KEY (snapshot_id, source_file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT uq_build_config_snapshot_build_config_id
        UNIQUE (snapshot_id, build_config_id),
    CONSTRAINT ck_build_config_argv_json_array
        CHECK (jsonb_typeof(argv_json) = 'array')
);

COMMENT ON TABLE build_config IS
    'Compilation command context for a translation unit, including argv, target, sysroot, and origin.';

CREATE UNIQUE INDEX ux_build_config_snapshot_source_argv_output
    ON build_config (snapshot_id, source_file_id, argv_hash, (COALESCE(output_path, '')));

CREATE INDEX ix_build_config_snapshot_source_file_id
    ON build_config (snapshot_id, source_file_id);

CREATE TABLE parse_context (
    parse_context_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    file_id BIGINT NOT NULL,
    build_config_id BIGINT NOT NULL,
    context_kind TEXT NOT NULL CHECK (context_kind IN ('translation_unit', 'header_view', 'module_unit')),
    pp_fingerprint TEXT,
    borrowed_from_build_config_id BIGINT,
    confidence NUMERIC(4,3) NOT NULL DEFAULT 1.000 CHECK (confidence >= 0 AND confidence <= 1),
    parse_errors_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    CONSTRAINT fk_parse_context_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_parse_context_file
        FOREIGN KEY (snapshot_id, file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_parse_context_build_config
        FOREIGN KEY (snapshot_id, build_config_id)
        REFERENCES build_config (snapshot_id, build_config_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_parse_context_borrowed_build_config
        FOREIGN KEY (snapshot_id, borrowed_from_build_config_id)
        REFERENCES build_config (snapshot_id, build_config_id)
        ON DELETE SET NULL,
    CONSTRAINT uq_parse_context_snapshot_parse_context_id
        UNIQUE (snapshot_id, parse_context_id),
    CONSTRAINT ck_parse_context_parse_errors_json
        CHECK (jsonb_typeof(parse_errors_json) IN ('array', 'object'))
);

COMMENT ON TABLE parse_context IS
    'File parse result bound to a specific build configuration and preprocessor fingerprint.';

CREATE INDEX ix_parse_context_snapshot_file_id
    ON parse_context (snapshot_id, file_id);

CREATE INDEX ix_parse_context_snapshot_build_config_id
    ON parse_context (snapshot_id, build_config_id);

-- =========================
-- Provenance
-- =========================

CREATE TABLE provenance (
    provenance_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    extractor_name TEXT NOT NULL,
    extraction_method TEXT NOT NULL,
    exactness TEXT NOT NULL CHECK (exactness IN ('exact', 'approximate', 'inferred')),
    confidence NUMERIC(4,3) NOT NULL DEFAULT 1.000 CHECK (confidence >= 0 AND confidence <= 1),
    evidence_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    CONSTRAINT ck_provenance_evidence_json
        CHECK (jsonb_typeof(evidence_json) = 'object')
);

COMMENT ON TABLE provenance IS
    'Extraction provenance and confidence metadata attached to facts across layers.';

-- =========================
-- Symbol Layer
-- =========================

CREATE TABLE symbol (
    symbol_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    stable_key TEXT NOT NULL,
    kind TEXT NOT NULL,
    name TEXT NOT NULL,
    qualified_name TEXT NOT NULL,
    parent_symbol_id BIGINT,
    visibility TEXT CHECK (visibility IN ('public', 'protected', 'private', 'internal', 'unknown')),
    template_kind TEXT NOT NULL DEFAULT 'non_template'
        CHECK (template_kind IN ('non_template', 'primary', 'partial_spec', 'full_spec', 'instantiation')),
    canonical_decl_id BIGINT,
    canonical_def_id BIGINT,
    is_exported BOOLEAN NOT NULL DEFAULT FALSE,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_symbol_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT uq_symbol_snapshot_symbol_id
        UNIQUE (snapshot_id, symbol_id),
    CONSTRAINT fk_symbol_parent
        FOREIGN KEY (snapshot_id, parent_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE SET NULL,
    CONSTRAINT fk_symbol_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT
);

COMMENT ON TABLE symbol IS
    'Canonical symbol identity per snapshot (stable key, hierarchy, visibility, and template role).';

CREATE UNIQUE INDEX ux_symbol_snapshot_stable_key
    ON symbol (snapshot_id, stable_key);

CREATE INDEX ix_symbol_snapshot_qualified_name
    ON symbol (snapshot_id, qualified_name);

CREATE INDEX ix_symbol_snapshot_parent_symbol_id
    ON symbol (snapshot_id, parent_symbol_id);

CREATE TABLE symbol_decl (
    decl_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    symbol_id BIGINT NOT NULL,
    file_id BIGINT NOT NULL,
    parse_context_id BIGINT,
    role TEXT NOT NULL CHECK (role IN ('decl', 'def', 'fwd_decl')),
    span JSONB NOT NULL,
    signature_text TEXT,
    type_text TEXT,
    doc_comment TEXT,
    is_implicit BOOLEAN NOT NULL DEFAULT FALSE,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_symbol_decl_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT uq_symbol_decl_snapshot_decl_id
        UNIQUE (snapshot_id, decl_id),
    CONSTRAINT fk_symbol_decl_symbol
        FOREIGN KEY (snapshot_id, symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_symbol_decl_file
        FOREIGN KEY (snapshot_id, file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_symbol_decl_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE SET NULL,
    CONSTRAINT fk_symbol_decl_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_symbol_decl_span_json
        CHECK (jsonb_typeof(span) = 'object')
);

COMMENT ON TABLE symbol_decl IS
    'Concrete declaration/definition records with source spans, signatures, and optional docs.';

CREATE INDEX ix_symbol_decl_symbol_id ON symbol_decl (symbol_id);
CREATE INDEX ix_symbol_decl_file_id ON symbol_decl (file_id);
CREATE INDEX ix_symbol_decl_parse_context_id ON symbol_decl (parse_context_id);
CREATE INDEX ix_symbol_decl_snapshot_symbol_id ON symbol_decl (snapshot_id, symbol_id);
CREATE INDEX ix_symbol_decl_snapshot_file_id ON symbol_decl (snapshot_id, file_id);

ALTER TABLE symbol
    ADD CONSTRAINT fk_symbol_canonical_decl
    FOREIGN KEY (canonical_decl_id)
    REFERENCES symbol_decl (decl_id)
    ON DELETE SET NULL;

ALTER TABLE symbol
    ADD CONSTRAINT fk_symbol_canonical_def
    FOREIGN KEY (canonical_def_id)
    REFERENCES symbol_decl (decl_id)
    ON DELETE SET NULL;

CREATE TABLE relation (
    relation_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    parse_context_id BIGINT NOT NULL,
    from_symbol_id BIGINT NOT NULL,
    to_symbol_id BIGINT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('contains', 'inherits', 'overrides', 'specializes', 'instantiates', 'aliases', 'friend_of', 'constrained_by')),
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_relation_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_relation_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_relation_from_symbol
        FOREIGN KEY (snapshot_id, from_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_relation_to_symbol
        FOREIGN KEY (snapshot_id, to_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_relation_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_relation_no_dupes
        UNIQUE (snapshot_id, parse_context_id, from_symbol_id, to_symbol_id, kind)
);

COMMENT ON TABLE relation IS
    'Typed semantic edges between symbols (containment, inheritance, specialization, override, etc.).';

CREATE INDEX ix_relation_snapshot_from_symbol_id ON relation (snapshot_id, from_symbol_id);
CREATE INDEX ix_relation_snapshot_to_symbol_id ON relation (snapshot_id, to_symbol_id);

CREATE TABLE occurrence (
    snapshot_id BIGINT NOT NULL,
    occurrence_id BIGINT GENERATED ALWAYS AS IDENTITY,
    parse_context_id BIGINT NOT NULL,
    file_id BIGINT NOT NULL,
    symbol_id BIGINT NOT NULL,
    span JSONB NOT NULL,
    role_bits BIGINT NOT NULL DEFAULT 0 CHECK (role_bits >= 0),
    via_macro BOOLEAN NOT NULL DEFAULT FALSE,
    is_implicit BOOLEAN NOT NULL DEFAULT FALSE,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT pk_occurrence PRIMARY KEY (snapshot_id, occurrence_id),
    CONSTRAINT fk_occurrence_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_occurrence_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_occurrence_file
        FOREIGN KEY (snapshot_id, file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_occurrence_symbol
        FOREIGN KEY (snapshot_id, symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_occurrence_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_occurrence_span_json
        CHECK (jsonb_typeof(span) = 'object')
) PARTITION BY HASH (snapshot_id);

COMMENT ON TABLE occurrence IS
    'Symbol reference/use occurrences with role bitmasks and macro/implicit attribution.';

-- =========================
-- C++ Specific Tables
-- =========================

CREATE TABLE type_edge (
    type_edge_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    owner_symbol_id BIGINT NOT NULL,
    target_symbol_id BIGINT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('return_type', 'param_type', 'field_type', 'base_type', 'alias_target', 'template_arg', 'constraint')),
    ordinal INTEGER NOT NULL DEFAULT 0 CHECK (ordinal >= 0),
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_type_edge_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_type_edge_owner_symbol
        FOREIGN KEY (snapshot_id, owner_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_type_edge_target_symbol
        FOREIGN KEY (snapshot_id, target_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_type_edge_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_type_edge_no_dupes
        UNIQUE (snapshot_id, owner_symbol_id, target_symbol_id, kind, ordinal)
);

COMMENT ON TABLE type_edge IS
    'Typed edges from owning symbols to referenced type symbols (return, param, base, template args, etc.).';

CREATE INDEX ix_type_edge_snapshot_owner_symbol_id ON type_edge (snapshot_id, owner_symbol_id);
CREATE INDEX ix_type_edge_snapshot_target_symbol_id ON type_edge (snapshot_id, target_symbol_id);

CREATE TABLE file_dependency (
    dep_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    parse_context_id BIGINT NOT NULL,
    from_file_id BIGINT NOT NULL,
    to_file_id BIGINT NOT NULL,
    directive_kind TEXT NOT NULL CHECK (directive_kind IN ('include', 'import', 'module_import', 'header_unit')),
    literal_text TEXT,
    span JSONB NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_file_dependency_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_file_dependency_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_file_dependency_from_file
        FOREIGN KEY (snapshot_id, from_file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_file_dependency_to_file
        FOREIGN KEY (snapshot_id, to_file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_file_dependency_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_file_dependency_span_json
        CHECK (jsonb_typeof(span) = 'object')
);

COMMENT ON TABLE file_dependency IS
    'Include/import/module dependency edges between files in a parse context.';

CREATE TABLE callsite (
    snapshot_id BIGINT NOT NULL,
    callsite_id BIGINT GENERATED ALWAYS AS IDENTITY,
    parse_context_id BIGINT NOT NULL,
    file_id BIGINT NOT NULL,
    caller_symbol_id BIGINT NOT NULL,
    span JSONB NOT NULL,
    dispatch_kind TEXT NOT NULL CHECK (dispatch_kind IN ('direct', 'virtual', 'funcptr', 'ctor', 'dtor', 'operator', 'unresolved')),
    raw_text TEXT,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT pk_callsite PRIMARY KEY (snapshot_id, callsite_id),
    CONSTRAINT fk_callsite_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_callsite_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_callsite_file
        FOREIGN KEY (snapshot_id, file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_callsite_caller_symbol
        FOREIGN KEY (snapshot_id, caller_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_callsite_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_callsite_span_json
        CHECK (jsonb_typeof(span) = 'object')
) PARTITION BY HASH (snapshot_id);

COMMENT ON TABLE callsite IS
    'Call expression sites with dispatch classification and source evidence.';

CREATE TABLE call_target (
    snapshot_id BIGINT NOT NULL,
    callsite_id BIGINT NOT NULL,
    callee_symbol_id BIGINT NOT NULL,
    rank INTEGER NOT NULL DEFAULT 1 CHECK (rank >= 1),
    resolution_kind TEXT NOT NULL CHECK (resolution_kind IN ('direct', 'virtual_candidate', 'overload_candidate', 'funcptr_candidate', 'unknown')),
    confidence NUMERIC(4,3) NOT NULL DEFAULT 1.000 CHECK (confidence >= 0 AND confidence <= 1),
    provenance_id BIGINT NOT NULL,
    CONSTRAINT pk_call_target PRIMARY KEY (snapshot_id, callsite_id, callee_symbol_id, rank),
    CONSTRAINT fk_call_target_callsite
        FOREIGN KEY (snapshot_id, callsite_id)
        REFERENCES callsite (snapshot_id, callsite_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_call_target_callee_symbol
        FOREIGN KEY (snapshot_id, callee_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_call_target_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT
) PARTITION BY HASH (snapshot_id);

COMMENT ON TABLE call_target IS
    'Resolved candidate callees per callsite with ranked resolution kind and confidence.';

CREATE TABLE macro_event (
    snapshot_id BIGINT NOT NULL,
    macro_event_id BIGINT GENERATED ALWAYS AS IDENTITY,
    parse_context_id BIGINT NOT NULL,
    macro_symbol_id BIGINT NOT NULL,
    file_id BIGINT NOT NULL,
    span JSONB NOT NULL,
    event_kind TEXT NOT NULL CHECK (event_kind IN ('define', 'undef', 'expand')),
    expansion_text_hash TEXT,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT pk_macro_event PRIMARY KEY (snapshot_id, macro_event_id),
    CONSTRAINT fk_macro_event_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_macro_event_parse_context
        FOREIGN KEY (snapshot_id, parse_context_id)
        REFERENCES parse_context (snapshot_id, parse_context_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_macro_event_macro_symbol
        FOREIGN KEY (snapshot_id, macro_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_macro_event_file
        FOREIGN KEY (snapshot_id, file_id)
        REFERENCES file (snapshot_id, file_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_macro_event_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_macro_event_span_json
        CHECK (jsonb_typeof(span) = 'object')
) PARTITION BY HASH (snapshot_id);

COMMENT ON TABLE macro_event IS
    'Preprocessor macro define/undef/expand events captured per parse context.';

-- Create hash partitions (16) for large fact tables keyed by snapshot_id.
DO $$
DECLARE
    i INTEGER;
BEGIN
    FOR i IN 0..15 LOOP
        EXECUTE format(
            'CREATE TABLE occurrence_p%s PARTITION OF occurrence FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            lpad(i::text, 2, '0'),
            i
        );

        EXECUTE format(
            'CREATE TABLE callsite_p%s PARTITION OF callsite FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            lpad(i::text, 2, '0'),
            i
        );

        EXECUTE format(
            'CREATE TABLE call_target_p%s PARTITION OF call_target FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            lpad(i::text, 2, '0'),
            i
        );

        EXECUTE format(
            'CREATE TABLE macro_event_p%s PARTITION OF macro_event FOR VALUES WITH (MODULUS 16, REMAINDER %s);',
            lpad(i::text, 2, '0'),
            i
        );
    END LOOP;
END;
$$;

-- =========================
-- Flow Layer
-- =========================

CREATE TABLE callable_port (
    port_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    callable_symbol_id BIGINT NOT NULL,
    port_kind TEXT NOT NULL CHECK (port_kind IN ('this', 'param', 'return', 'field', 'global', 'capture')),
    label TEXT NOT NULL,
    ordinal INTEGER NOT NULL DEFAULT 0 CHECK (ordinal >= 0),
    pointee_depth INTEGER NOT NULL DEFAULT 0 CHECK (pointee_depth >= 0),
    type_symbol_id BIGINT,
    CONSTRAINT fk_callable_port_callable_symbol
        FOREIGN KEY (callable_symbol_id)
        REFERENCES symbol (symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_callable_port_type_symbol
        FOREIGN KEY (type_symbol_id)
        REFERENCES symbol (symbol_id)
        ON DELETE SET NULL,
    CONSTRAINT uq_callable_port
        UNIQUE (callable_symbol_id, port_kind, ordinal, label)
);

COMMENT ON TABLE callable_port IS
    'Stable callable boundary ports (this/params/return/field/global/capture) used by flow summaries.';

CREATE INDEX ix_callable_port_callable_symbol_id ON callable_port (callable_symbol_id);

CREATE TABLE flow_summary (
    flow_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    callable_symbol_id BIGINT NOT NULL,
    from_port_id BIGINT NOT NULL,
    to_port_id BIGINT NOT NULL,
    flow_kind TEXT NOT NULL CHECK (flow_kind IN ('value', 'taint', 'alias', 'store', 'load', 'return', 'escape', 'ownership_transfer', 'mutates')),
    condition_kind TEXT NOT NULL CHECK (condition_kind IN ('always', 'may', 'nonnull', 'success_path', 'error_path')),
    engine TEXT NOT NULL CHECK (engine IN ('local_ast', 'codeql_ir', 'manual_model')),
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_flow_summary_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_flow_summary_callable_symbol
        FOREIGN KEY (snapshot_id, callable_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_flow_summary_from_port
        FOREIGN KEY (from_port_id)
        REFERENCES callable_port (port_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_flow_summary_to_port
        FOREIGN KEY (to_port_id)
        REFERENCES callable_port (port_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_flow_summary_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_flow_summary_no_dupes
        UNIQUE (snapshot_id, callable_symbol_id, from_port_id, to_port_id, flow_kind, condition_kind, engine)
);

COMMENT ON TABLE flow_summary IS
    'Precomputed 1-hop flow edges between callable ports (value/taint/ownership/etc.).';

CREATE TABLE effect_summary (
    effect_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    snapshot_id BIGINT NOT NULL,
    callable_symbol_id BIGINT NOT NULL,
    effect_kind TEXT NOT NULL CHECK (effect_kind IN ('reads_global', 'writes_global', 'allocates', 'frees', 'locks', 'unlocks', 'throws', 'blocks', 'starts_thread', 'io')),
    target_symbol_id BIGINT,
    provenance_id BIGINT NOT NULL,
    CONSTRAINT fk_effect_summary_snapshot
        FOREIGN KEY (snapshot_id)
        REFERENCES snapshot (snapshot_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_effect_summary_callable_symbol
        FOREIGN KEY (snapshot_id, callable_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE CASCADE,
    CONSTRAINT fk_effect_summary_target_symbol
        FOREIGN KEY (snapshot_id, target_symbol_id)
        REFERENCES symbol (snapshot_id, symbol_id)
        ON DELETE SET NULL,
    CONSTRAINT fk_effect_summary_provenance
        FOREIGN KEY (provenance_id)
        REFERENCES provenance (provenance_id)
        ON DELETE RESTRICT,
    CONSTRAINT uq_effect_summary_no_dupes
        UNIQUE (snapshot_id, callable_symbol_id, effect_kind, target_symbol_id)
);

COMMENT ON TABLE effect_summary IS
    'Callable effect facts (global reads/writes, allocation, locking, exceptions, IO, threading).';

CREATE INDEX ix_effect_summary_snapshot_callable_symbol_id
    ON effect_summary (snapshot_id, callable_symbol_id);

CREATE TABLE external_flow_model (
    model_id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    library_name TEXT NOT NULL,
    symbol_matcher TEXT NOT NULL,
    from_port_spec TEXT,
    to_port_spec TEXT,
    model_kind TEXT NOT NULL CHECK (model_kind IN ('source', 'sink', 'summary')),
    origin TEXT NOT NULL CHECK (origin IN ('manual', 'imported_codeql_model'))
);

COMMENT ON TABLE external_flow_model IS
    'Out-of-repo/library flow models describing source/sink/summary behavior for unresolved external code.';

CREATE INDEX ix_external_flow_model_library_name ON external_flow_model (library_name);

CREATE UNIQUE INDEX ux_external_flow_model_no_dupes
    ON external_flow_model (
        library_name,
        symbol_matcher,
        (COALESCE(from_port_spec, '')),
        (COALESCE(to_port_spec, '')),
        model_kind,
        origin
    );

-- =========================
-- MCP3-specified Indexes
-- =========================

CREATE INDEX ix_occurrence_symbol_parse_context
    ON occurrence (symbol_id, parse_context_id);

CREATE INDEX ix_callsite_caller_symbol_id
    ON callsite (caller_symbol_id);

CREATE INDEX ix_call_target_callee_symbol_id
    ON call_target (callee_symbol_id);

CREATE INDEX ix_file_dependency_from_file_id
    ON file_dependency (from_file_id);

CREATE INDEX ix_file_dependency_to_file_id
    ON file_dependency (to_file_id);

CREATE INDEX ix_flow_summary_callable_from_to
    ON flow_summary (callable_symbol_id, from_port_id, to_port_id);

-- =========================
-- Materialized Views
-- =========================

DROP MATERIALIZED VIEW IF EXISTS symbol_card_mv;
DROP MATERIALIZED VIEW IF EXISTS ref_rollup_mv;
DROP MATERIALIZED VIEW IF EXISTS call_rollup_mv;
DROP MATERIALIZED VIEW IF EXISTS impact_rollup_mv;
DROP MATERIALIZED VIEW IF EXISTS file_overview_mv;

CREATE MATERIALIZED VIEW ref_rollup_mv AS
SELECT
    o.snapshot_id,
    o.symbol_id,
    o.file_id,
    o.span,
    o.role_bits,
    o.via_macro,
    o.is_implicit,
    COUNT(DISTINCT o.parse_context_id)::BIGINT AS context_count,
    COUNT(*)::BIGINT AS support_count,
    COALESCE(MAX(p.confidence), 0.000)::NUMERIC(4,3) AS max_confidence
FROM occurrence o
LEFT JOIN provenance p
    ON p.provenance_id = o.provenance_id
GROUP BY
    o.snapshot_id,
    o.symbol_id,
    o.file_id,
    o.span,
    o.role_bits,
    o.via_macro,
    o.is_implicit;

COMMENT ON MATERIALIZED VIEW ref_rollup_mv IS
    'Deduplicated references across parse contexts with support and confidence rollups.';

CREATE INDEX ix_ref_rollup_mv_snapshot_symbol_id
    ON ref_rollup_mv (snapshot_id, symbol_id);

CREATE INDEX ix_ref_rollup_mv_snapshot_file_id
    ON ref_rollup_mv (snapshot_id, file_id);

CREATE MATERIALIZED VIEW call_rollup_mv AS
SELECT
    cs.snapshot_id,
    cs.caller_symbol_id,
    ct.callee_symbol_id,
    COUNT(DISTINCT cs.callsite_id)::BIGINT AS callsite_count,
    COUNT(*)::BIGINT AS candidate_count,
    MAX(LEAST(ct.confidence, COALESCE(p.confidence, ct.confidence)))::NUMERIC(4,3) AS max_confidence,
    ARRAY_AGG(DISTINCT cs.dispatch_kind) AS dispatch_kinds,
    ARRAY_AGG(DISTINCT ct.resolution_kind) AS resolution_kinds,
    (ARRAY_AGG(DISTINCT cs.callsite_id ORDER BY cs.callsite_id))[1:5] AS representative_callsites
FROM callsite cs
JOIN call_target ct
    ON ct.snapshot_id = cs.snapshot_id
   AND ct.callsite_id = cs.callsite_id
LEFT JOIN provenance p
    ON p.provenance_id = ct.provenance_id
GROUP BY
    cs.snapshot_id,
    cs.caller_symbol_id,
    ct.callee_symbol_id;

COMMENT ON MATERIALIZED VIEW call_rollup_mv IS
    'Caller-to-callee aggregate edges with call counts, candidate evidence, and representative callsites.';

CREATE INDEX ix_call_rollup_mv_snapshot_caller_symbol_id
    ON call_rollup_mv (snapshot_id, caller_symbol_id);

CREATE INDEX ix_call_rollup_mv_snapshot_callee_symbol_id
    ON call_rollup_mv (snapshot_id, callee_symbol_id);

CREATE MATERIALIZED VIEW symbol_card_mv AS
WITH decl_stats AS (
    SELECT
        sd.snapshot_id,
        sd.symbol_id,
        COUNT(*) FILTER (WHERE sd.role = 'decl')::BIGINT AS decl_count,
        COUNT(*) FILTER (WHERE sd.role = 'def')::BIGINT AS def_count,
        COUNT(*) FILTER (WHERE sd.role = 'fwd_decl')::BIGINT AS fwd_decl_count,
        COALESCE(
            MAX(sd.signature_text) FILTER (WHERE sd.role = 'def' AND sd.signature_text IS NOT NULL),
            MAX(sd.signature_text) FILTER (WHERE sd.role = 'decl' AND sd.signature_text IS NOT NULL)
        ) AS signature_text
    FROM symbol_decl sd
    GROUP BY sd.snapshot_id, sd.symbol_id
),
effect_stats AS (
    SELECT
        es.snapshot_id,
        es.callable_symbol_id AS symbol_id,
        ARRAY_AGG(DISTINCT es.effect_kind ORDER BY es.effect_kind) AS key_effects
    FROM effect_summary es
    GROUP BY es.snapshot_id, es.callable_symbol_id
),
macro_stats AS (
    SELECT
        me.snapshot_id,
        me.macro_symbol_id AS symbol_id,
        COUNT(*)::BIGINT AS macro_hazard_count,
        COUNT(*) FILTER (WHERE me.event_kind = 'expand')::BIGINT AS macro_expand_count
    FROM macro_event me
    GROUP BY me.snapshot_id, me.macro_symbol_id
)
SELECT
    s.snapshot_id,
    s.symbol_id,
    s.stable_key,
    s.kind,
    s.name,
    s.qualified_name,
    s.visibility,
    s.template_kind,
    s.is_exported,
    s.parent_symbol_id AS owner_symbol_id,
    owner.qualified_name AS owner_qualified_name,
    s.canonical_decl_id,
    s.canonical_def_id,
    COALESCE(ds.signature_text, '') AS signature_text,
    COALESCE(ds.decl_count, 0) AS decl_count,
    COALESCE(ds.def_count, 0) AS def_count,
    COALESCE(ds.fwd_decl_count, 0) AS fwd_decl_count,
    COALESCE(callers.top_callers, '[]'::jsonb) AS top_callers,
    COALESCE(callees.top_callees, '[]'::jsonb) AS top_callees,
    COALESCE(refs.top_refs, '[]'::jsonb) AS top_refs,
    COALESCE(eff.key_effects, ARRAY[]::TEXT[]) AS key_effects,
    COALESCE(ms.macro_hazard_count, 0) AS macro_hazard_count,
    COALESCE(ms.macro_expand_count, 0) AS macro_expand_count
FROM symbol s
LEFT JOIN symbol owner
    ON owner.symbol_id = s.parent_symbol_id
LEFT JOIN decl_stats ds
    ON ds.snapshot_id = s.snapshot_id
   AND ds.symbol_id = s.symbol_id
LEFT JOIN effect_stats eff
    ON eff.snapshot_id = s.snapshot_id
   AND eff.symbol_id = s.symbol_id
LEFT JOIN macro_stats ms
    ON ms.snapshot_id = s.snapshot_id
   AND ms.symbol_id = s.symbol_id
LEFT JOIN LATERAL (
    SELECT
        jsonb_agg(
            jsonb_build_object(
                'caller_symbol_id', ranked.caller_symbol_id,
                'callsite_count', ranked.callsite_count,
                'max_confidence', ranked.max_confidence
            )
            ORDER BY ranked.callsite_count DESC, ranked.caller_symbol_id
        ) AS top_callers
    FROM (
        SELECT
            cr.caller_symbol_id,
            cr.callsite_count,
            cr.max_confidence
        FROM call_rollup_mv cr
        WHERE cr.snapshot_id = s.snapshot_id
          AND cr.callee_symbol_id = s.symbol_id
        ORDER BY cr.callsite_count DESC, cr.caller_symbol_id
        LIMIT 5
    ) ranked
) callers ON TRUE
LEFT JOIN LATERAL (
    SELECT
        jsonb_agg(
            jsonb_build_object(
                'callee_symbol_id', ranked.callee_symbol_id,
                'callsite_count', ranked.callsite_count,
                'max_confidence', ranked.max_confidence
            )
            ORDER BY ranked.callsite_count DESC, ranked.callee_symbol_id
        ) AS top_callees
    FROM (
        SELECT
            cr.callee_symbol_id,
            cr.callsite_count,
            cr.max_confidence
        FROM call_rollup_mv cr
        WHERE cr.snapshot_id = s.snapshot_id
          AND cr.caller_symbol_id = s.symbol_id
        ORDER BY cr.callsite_count DESC, cr.callee_symbol_id
        LIMIT 5
    ) ranked
) callees ON TRUE
LEFT JOIN LATERAL (
    SELECT
        jsonb_agg(
            jsonb_build_object(
                'file_id', ranked.file_id,
                'support_count', ranked.support_count,
                'max_confidence', ranked.max_confidence
            )
            ORDER BY ranked.support_count DESC, ranked.file_id
        ) AS top_refs
    FROM (
        SELECT
            rr.file_id,
            SUM(rr.support_count)::BIGINT AS support_count,
            MAX(rr.max_confidence)::NUMERIC(4,3) AS max_confidence
        FROM ref_rollup_mv rr
        WHERE rr.snapshot_id = s.snapshot_id
          AND rr.symbol_id = s.symbol_id
        GROUP BY rr.file_id
        ORDER BY support_count DESC, rr.file_id
        LIMIT 5
    ) ranked
) refs ON TRUE;

COMMENT ON MATERIALIZED VIEW symbol_card_mv IS
    'One-row symbol dashboard with declaration stats, top callers/callees/refs, effects, and macro hazards.';

CREATE UNIQUE INDEX ux_symbol_card_mv_snapshot_symbol_id
    ON symbol_card_mv (snapshot_id, symbol_id);

CREATE MATERIALIZED VIEW impact_rollup_mv AS
WITH symbol_rollup AS (
    SELECT
        s.snapshot_id,
        'symbol'::TEXT AS owner_kind,
        s.symbol_id AS owner_id,
        COALESCE(ir.incoming_refs, 0) AS incoming_refs,
        COALESCE(oc.outgoing_calls, 0) AS outgoing_calls,
        COALESCE(dt.dependent_types, 0) AS dependent_types,
        COALESCE(dh.dependent_headers, 0) AS dependent_headers,
        0::BIGINT AS dependent_components
    FROM symbol s
    LEFT JOIN (
        SELECT
            rr.snapshot_id,
            rr.symbol_id,
            SUM(rr.support_count)::BIGINT AS incoming_refs
        FROM ref_rollup_mv rr
        GROUP BY rr.snapshot_id, rr.symbol_id
    ) ir
        ON ir.snapshot_id = s.snapshot_id
       AND ir.symbol_id = s.symbol_id
    LEFT JOIN (
        SELECT
            cr.snapshot_id,
            cr.caller_symbol_id AS symbol_id,
            SUM(cr.callsite_count)::BIGINT AS outgoing_calls
        FROM call_rollup_mv cr
        GROUP BY cr.snapshot_id, cr.caller_symbol_id
    ) oc
        ON oc.snapshot_id = s.snapshot_id
       AND oc.symbol_id = s.symbol_id
    LEFT JOIN (
        SELECT
            te.snapshot_id,
            te.target_symbol_id AS symbol_id,
            COUNT(*)::BIGINT AS dependent_types
        FROM type_edge te
        GROUP BY te.snapshot_id, te.target_symbol_id
    ) dt
        ON dt.snapshot_id = s.snapshot_id
       AND dt.symbol_id = s.symbol_id
    LEFT JOIN (
        SELECT
            sd.snapshot_id,
            sd.symbol_id,
            COUNT(DISTINCT fd.to_file_id)::BIGINT AS dependent_headers
        FROM symbol_decl sd
        JOIN file_dependency fd
          ON fd.snapshot_id = sd.snapshot_id
         AND fd.from_file_id = sd.file_id
        GROUP BY sd.snapshot_id, sd.symbol_id
    ) dh
        ON dh.snapshot_id = s.snapshot_id
       AND dh.symbol_id = s.symbol_id
),
file_rollup AS (
    SELECT
        f.snapshot_id,
        'file'::TEXT AS owner_kind,
        f.file_id AS owner_id,
        COALESCE(fr.incoming_refs, 0) AS incoming_refs,
        COALESCE(fc.outgoing_calls, 0) AS outgoing_calls,
        COALESCE(ft.dependent_types, 0) AS dependent_types,
        COALESCE(fd.dependent_headers, 0) AS dependent_headers,
        0::BIGINT AS dependent_components
    FROM file f
    LEFT JOIN (
        SELECT
            o.snapshot_id,
            o.file_id,
            COUNT(*)::BIGINT AS incoming_refs
        FROM occurrence o
        GROUP BY o.snapshot_id, o.file_id
    ) fr
        ON fr.snapshot_id = f.snapshot_id
       AND fr.file_id = f.file_id
    LEFT JOIN (
        SELECT
            cs.snapshot_id,
            cs.file_id,
            COUNT(*)::BIGINT AS outgoing_calls
        FROM callsite cs
        GROUP BY cs.snapshot_id, cs.file_id
    ) fc
        ON fc.snapshot_id = f.snapshot_id
       AND fc.file_id = f.file_id
    LEFT JOIN (
        SELECT
            sd.snapshot_id,
            sd.file_id,
            COUNT(te.type_edge_id)::BIGINT AS dependent_types
        FROM symbol_decl sd
        JOIN symbol s
          ON s.snapshot_id = sd.snapshot_id
         AND s.symbol_id = sd.symbol_id
        LEFT JOIN type_edge te
          ON te.snapshot_id = sd.snapshot_id
         AND te.owner_symbol_id = s.symbol_id
        GROUP BY sd.snapshot_id, sd.file_id
    ) ft
        ON ft.snapshot_id = f.snapshot_id
       AND ft.file_id = f.file_id
    LEFT JOIN (
        SELECT
            fd.snapshot_id,
            fd.from_file_id AS file_id,
            COUNT(DISTINCT fd.to_file_id)::BIGINT AS dependent_headers
        FROM file_dependency fd
        GROUP BY fd.snapshot_id, fd.from_file_id
    ) fd
        ON fd.snapshot_id = f.snapshot_id
       AND fd.file_id = f.file_id
)
SELECT * FROM symbol_rollup
UNION ALL
SELECT * FROM file_rollup;

COMMENT ON MATERIALIZED VIEW impact_rollup_mv IS
    'Per symbol/file impact rollup: refs, calls, type dependencies, and header dependencies.';

CREATE INDEX ix_impact_rollup_mv_snapshot_owner
    ON impact_rollup_mv (snapshot_id, owner_kind, owner_id);

CREATE MATERIALIZED VIEW file_overview_mv AS
WITH dep_rank AS (
    SELECT
        fd.snapshot_id,
        fd.from_file_id AS file_id,
        fd.to_file_id,
        COUNT(*)::BIGINT AS support_count,
        ROW_NUMBER() OVER (
            PARTITION BY fd.snapshot_id, fd.from_file_id
            ORDER BY COUNT(*) DESC, fd.to_file_id
        ) AS rn
    FROM file_dependency fd
    JOIN file tf
      ON tf.snapshot_id = fd.snapshot_id
     AND tf.file_id = fd.to_file_id
    WHERE fd.is_active = TRUE
      AND tf.is_external = TRUE
    GROUP BY fd.snapshot_id, fd.from_file_id, fd.to_file_id
),
external_dep_json AS (
    SELECT
        dr.snapshot_id,
        dr.file_id,
        jsonb_agg(
            jsonb_build_object(
                'to_file_id', dr.to_file_id,
                'path', tf.path,
                'support_count', dr.support_count
            )
            ORDER BY dr.support_count DESC, dr.to_file_id
        ) AS top_external_dependencies
    FROM dep_rank dr
    JOIN file tf
      ON tf.snapshot_id = dr.snapshot_id
     AND tf.file_id = dr.to_file_id
    WHERE dr.rn <= 5
    GROUP BY dr.snapshot_id, dr.file_id
),
include_stats AS (
    SELECT
        fd.snapshot_id,
        fd.from_file_id AS file_id,
        COUNT(*) FILTER (WHERE fd.directive_kind = 'include')::BIGINT AS include_count,
        COUNT(*) FILTER (WHERE fd.directive_kind IN ('import', 'module_import', 'header_unit'))::BIGINT AS import_count
    FROM file_dependency fd
    WHERE fd.is_active = TRUE
    GROUP BY fd.snapshot_id, fd.from_file_id
),
def_stats AS (
    SELECT
        sd.snapshot_id,
        sd.file_id,
        COUNT(DISTINCT sd.symbol_id)::BIGINT AS defined_symbol_count
    FROM symbol_decl sd
    WHERE sd.role = 'def'
    GROUP BY sd.snapshot_id, sd.file_id
),
ref_stats AS (
    SELECT
        o.snapshot_id,
        o.file_id,
        COUNT(DISTINCT o.symbol_id)::BIGINT AS referenced_symbol_count
    FROM occurrence o
    GROUP BY o.snapshot_id, o.file_id
),
macro_stats AS (
    SELECT
        me.snapshot_id,
        me.file_id,
        COUNT(*)::BIGINT AS macro_event_count
    FROM macro_event me
    GROUP BY me.snapshot_id, me.file_id
),
parse_stats AS (
    SELECT
        pc.snapshot_id,
        pc.file_id,
        COUNT(*)::BIGINT AS parse_context_count,
        COUNT(*) FILTER (
            WHERE jsonb_typeof(pc.parse_errors_json) = 'array'
              AND jsonb_array_length(pc.parse_errors_json) = 0
        )::BIGINT AS clean_parse_count
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
    COALESCE(ins.include_count, 0) AS include_count,
    COALESCE(ins.import_count, 0) AS import_count,
    COALESCE(ds.defined_symbol_count, 0) AS defined_symbol_count,
    COALESCE(rs.referenced_symbol_count, 0) AS referenced_symbol_count,
    COALESCE(ed.top_external_dependencies, '[]'::jsonb) AS top_external_dependencies,
    COALESCE(ms.macro_event_count, 0) AS macro_event_count,
    CASE
        WHEN f.line_count > 0 THEN ROUND(COALESCE(ms.macro_event_count, 0)::NUMERIC / f.line_count, 6)
        ELSE 0::NUMERIC
    END AS macro_density,
    COALESCE(ps.parse_context_count, 0) AS parse_context_count,
    CASE
        WHEN COALESCE(ps.parse_context_count, 0) > 0
            THEN ROUND(ps.clean_parse_count::NUMERIC / ps.parse_context_count, 6)
        ELSE 0::NUMERIC
    END AS parse_coverage
FROM file f
LEFT JOIN include_stats ins
    ON ins.snapshot_id = f.snapshot_id
   AND ins.file_id = f.file_id
LEFT JOIN def_stats ds
    ON ds.snapshot_id = f.snapshot_id
   AND ds.file_id = f.file_id
LEFT JOIN ref_stats rs
    ON rs.snapshot_id = f.snapshot_id
   AND rs.file_id = f.file_id
LEFT JOIN external_dep_json ed
    ON ed.snapshot_id = f.snapshot_id
   AND ed.file_id = f.file_id
LEFT JOIN macro_stats ms
    ON ms.snapshot_id = f.snapshot_id
   AND ms.file_id = f.file_id
LEFT JOIN parse_stats ps
    ON ps.snapshot_id = f.snapshot_id
   AND ps.file_id = f.file_id;

COMMENT ON MATERIALIZED VIEW file_overview_mv IS
    'Per-file dashboard of includes/imports, symbol activity, external deps, macro density, and parse coverage.';

CREATE UNIQUE INDEX ux_file_overview_mv_snapshot_file_id
    ON file_overview_mv (snapshot_id, file_id);

COMMIT;
