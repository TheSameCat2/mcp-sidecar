# SQLite Lite Schema -- MCP Sidecar
-- Single-file database, no server process
-- Embedded in .NET via Microsoft.Data.Sqlite
-- Zero external dependencies (besides clangd)

-- Same functionality, slower queries acceptable at single-user scale

CREATE table snapshot (
    snapshot_id integer primary key autoincrement,
    repo_root text not null,
    vcs_commit text,
    workspace_hash text not null,
    parent_snapshot_id integer references snapshot(snapshot_id),
    kind text not null check(kind in ('background', 'overlay', 'imported')),
    created_at text default current_timestamp,
    last_updated_at text default current_timestamp,
    index_status text not null default 'in_progress' check(index_status in ('in_progress', 'complete', 'failed'))
);

create index ix_snapshot_repo_root on snapshot(repo_root);
create index ix_snapshot_workspace_hash on snapshot(workspace_hash);

create table file (
    file_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    path text not null,
    real_path text,
    content_hash text,
    language text not null check(language in ('c', 'c++', 'header', 'module_interface', 'module_impl')),
    is_generated integer not null default 0,
    is_external integer not null default 0,
    size_bytes integer not null default 0,
    line_count integer not null default 0,
    unique(snapshot_id, path)
);
create index ix_file_snapshot_path on file(snapshot_id, path);

create table build_config (
    build_config_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    source_file_id integer not null references file(snapshot_id, file_id) on delete cascade,
    output_path text,
    working_directory text not null,
    argv_json text not null,  -- JSON array as text
    argv_hash text not null,
    compiler text,
    language_standard text,
    target_triple text,
    sysroot text,
    defines_hash text,
    include_paths_hash text,
    command_origin text not null check(command_origin in ('exact', 'inferred', 'borrowed', 'imported'))
    command_text text,
    foreign key (snapshot_id, source_file_id, argv_hash, output_path) unique
);
create index ix_build_config_snapshot_file on build_config(snapshot_id, source_file_id);

create table parse_context (
    parse_context_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    file_id integer not null references file(snapshot_id, file_id) on delete cascade,
    build_config_id integer not null references build_config(snapshot_id, build_config_id) on delete cascade,
    context_kind text not null check(context_kind in ('translation_unit', 'header_view', 'module_unit')),
    pp_fingerprint text,
    borrowed_from_build_config_id integer,
    confidence real not null default 1.0 check(confidence >= 0 and confidence <= 1),
    parse_errors_json text not null default '[]',  -- JSON array
    foreign key (snapshot_id, file_id) references parse_context(snapshot_id, file_id) on delete cascade
);
create index ix_parse_context_snapshot_file on parse_context(snapshot_id, file_id);

create table provenance (
    provenance_id integer primary key autoincrement,
    extractor_name text not null,
    extraction_method text not null,
    exactness text not null check(exactness in ('exact', 'approximate', 'inferred')),
    confidence real not null default 1.0 check(confidence >= 0 and confidence <= 1),
    evidence_json text not null default '{}'  -- JSON object
);

create table symbol (
    symbol_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    stable_key text not null,
    kind text not null,
    name text not null,
    qualified_name text not null,
    parent_symbol_id integer references symbol(symbol_id) on delete set null,
    visibility text check(visibility in ('public', 'protected', 'private', 'internal', 'unknown')),
    template_kind text not null default 'non_template' check(template_kind in ('non_template', 'primary', 'partial_spec', 'full_spec', 'instantiation')),
    canonical_decl_id integer references symbol_decl(decl_id) on delete set null,
    canonical_def_id integer references symbol_decl(decl_id) on delete set null,
    is_exported integer not null default 0,
    provenance_id integer not null references provenance(provenance_id),
    unique(snapshot_id, stable_key)
);
create index ix_symbol_snapshot_qualified_name on symbol(snapshot_id, qualified_name);
create index ix_symbol_snapshot_name on symbol(snapshot_id, name);

create table symbol_decl (
    decl_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    symbol_id integer not null references symbol(snapshot_id, symbol_id) on delete cascade,
    file_id integer not null references file(snapshot_id, file_id) on delete cascade,
    parse_context_id integer references parse_context(snapshot_id, parse_context_id) on delete set null,
    role text not null check(role in ('decl', 'def', 'fwd_decl')),
    span text not null,  -- JSON object
    signature_text text,
    type_text text,
    doc_comment text,
    is_implicit integer not null default 0,
    provenance_id integer not null references provenance(provenance_id)
);
create index ix_symbol_decl_symbol_id on symbol_decl(symbol_id);
create index ix_symbol_decl_snapshot_file on symbol_decl(snapshot_id, file_id);

create table occurrence (
    occurrence_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    parse_context_id integer not null references parse_context(snapshot_id, parse_context_id) on delete cascade,
    file_id integer not null references file(snapshot_id, file_id) on delete cascade,
    symbol_id integer not null references symbol(snapshot_id, symbol_id) on delete cascade,
    span text not null,  -- JSON object
    role_bits integer not null default 0,
    via_macro integer not null default 0,
    is_implicit integer not null default 0,
    provenance_id integer not null references provenance(provenance_id)
);
create index ix_occurrence_snapshot_symbol on occurrence(snapshot_id, symbol_id);
create index ix_occurrence_snapshot_file on occurrence(snapshot_id, file_id);

create table relation (
    relation_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    from_symbol_id integer not null references symbol(snapshot_id, from_symbol_id) on delete cascade,
    to_symbol_id integer not null references symbol(snapshot_id, to_symbol_id) on delete cascade,
    kind text not null check(kind in ('contains', 'inherits', 'overrides', 'specializes', 'instantiates', 'aliases', 'friend_of', 'constrained_by')),
    provenance_id integer not null references provenance(provenance_id),
    unique(snapshot_id, from_symbol_id, to_symbol_id, kind)
);
create index ix_relation_from_symbol on relation(snapshot_id, from_symbol_id);
create index ix_relation_to_symbol on relation(snapshot_id, to_symbol_id);

create table type_edge (
    type_edge_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    owner_symbol_id integer not null references symbol(snapshot_id, owner_symbol_id) on delete cascade,
    target_symbol_id integer not null references symbol(snapshot_id, target_symbol_id) on delete cascade,
    kind text not null check(kind in ('return_type', 'param_type', 'field_type', 'base_type', 'alias_target', 'template_arg', 'constraint')),
    ordinal integer not null default 0,
    provenance_id integer not null references provenance(provenance_id),
    unique(snapshot_id, owner_symbol_id, target_symbol_id, kind, ordinal)
);
create index ix_type_edge_owner_symbol on type_edge(snapshot_id, owner_symbol_id);

create table file_dependency (
    dep_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    from_file_id integer not null references file(snapshot_id, from_file_id) on delete cascade,
    to_file_id integer not null references file(snapshot_id, to_file_id) on delete cascade,
    directive_kind text not null check(directive_kind in ('include', 'import', 'module_import', 'header_unit')),
    literal_text text,
    span text not null,
    is_active integer not null default 1,
    provenance_id integer not null references provenance(provenance_id)
);
create index ix_file_dependency_from_file on file_dependency(snapshot_id, from_file_id);

create table callsite (
    callsite_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    parse_context_id integer not null references parse_context(snapshot_id, parse_context_id) on delete cascade,
    file_id integer not null references file(snapshot_id, file_id) on delete cascade,
    caller_symbol_id integer not null references symbol(snapshot_id, caller_symbol_id) on delete cascade,
    span text not null,
    dispatch_kind text not null check(dispatch_kind in ('direct', 'virtual', 'funcptr', 'ctor', 'dtor', 'operator', 'unresolved')),
    raw_text text,
    provenance_id integer not null references provenance(provenance_id)
);
create index ix_callsite_caller_symbol on callsite(snapshot_id, caller_symbol_id);

create table call_target (
    snapshot_id integer not null,
    callsite_id integer not null references callsite(snapshot_id, callsite_id) on delete cascade,
    callee_symbol_id integer not null references symbol(snapshot_id, callee_symbol_id) on delete cascade,
    rank integer not null default 1,
    resolution_kind text not null check(resolution_kind in ('direct', 'virtual_candidate', 'overload_candidate', 'funcptr_candidate', 'unknown')),
    confidence real not null default 1.0 check(confidence >= 0 and confidence <= 1),
    provenance_id integer not null references provenance(provenance_id),
    primary key (snapshot_id, callsite_id, callee_symbol_id, rank),
    foreign key (snapshot_id, callsite_id) references callsite(snapshot_id, callsite_id) on delete cascade
);

create index ix_call_target_callee on call_target(snapshot_id, callee_symbol_id);

create table callable_port (
    port_id integer primary key autoincrement,
    callable_symbol_id integer not null references symbol(symbol_id) on delete cascade,
    port_kind text not null check(port_kind in ('this', 'param', 'return', 'field', 'global', 'capture')),
    label text not null,
    ordinal integer not null default 0,
    pointee_depth integer not null default 0,
    type_symbol_id integer references symbol(symbol_id) on delete set null,
    unique(callable_symbol_id, port_kind, ordinal, label)
);

create table flow_summary (
    flow_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    callable_symbol_id integer not null references symbol(symbol_id) on delete cascade,
    from_port_id integer not null references callable_port(port_id) on delete cascade,
    to_port_id integer not null references callable_port(port_id) on delete cascade,
    flow_kind text not null check(flow_kind in ('value', 'taint', 'alias', 'store', 'load', 'return', 'escape', 'ownership_transfer', 'mutates')),
    condition_kind text not null check(condition_kind in ('always', 'may', 'nonnull', 'success_path', 'error_path')),
    engine text not null check(engine in ('local_ast', 'codeql_ir', 'manual_model')),
    provenance_id integer not null references provenance(provenance_id),
    unique(snapshot_id, callable_symbol_id, from_port_id, to_port_id, flow_kind, condition_kind, engine)
);

create table effect_summary (
    effect_id integer primary key autoincrement,
    snapshot_id integer not null references snapshot(snapshot_id) on delete cascade,
    callable_symbol_id integer not null references symbol(symbol_id) on delete cascade,
    effect_kind text not null check(effect_kind in ('reads_global', 'writes_global', 'allocates', 'frees', 'locks', 'unlocks', 'throws', 'blocks', 'starts_thread', 'io')),
    target_symbol_id integer references symbol(symbol_id) on delete set null,
    provenance_id integer not null references provenance(provenance_id),
    unique(snapshot_id, callable_symbol_id, effect_kind, target_symbol_id)
);

create table external_flow_model (
    model_id integer primary key autoincrement,
    library_name text not null,
    symbol_matcher text not null,
    from_port_spec text,
    to_port_spec text,
    model_kind text not null check(model_kind in ('source', 'sink', 'summary')),
    origin text not null check(origin in ('manual', 'imported_codeql_model'))
);

-- Precomputed rollup tables (refreshed on demand)
create table symbol_card (
    symbol_id integer primary key,
    snapshot_id integer not null,
    stable_key text not null,
    kind text not null,
    name text not null,
    qualified_name text not null,
    visibility text,
    template_kind text not null default 'non_template',
    owner_symbol_id integer,
    owner_qualified_name text,
    signature_text text,
    decl_count integer not null default 0,
    def_count integer not null default 0,
    fwd_decl_count integer not null default 0,
    top_callers text,  -- JSON array
    top_callees text,  -- JSON array
    top_refs text,  -- JSON array
    key_effects text,  -- JSON array
    refreshed_at integer
);

create index ix_symbol_card_snapshot_symbol on symbol_card(snapshot_id, symbol_id);
create unique(symbol_id) in symbol_card (snapshot_id, stable_key);

create table call_rollup (
    snapshot_id integer not null,
    caller_symbol_id integer not null,
    callee_symbol_id integer not null,
    callsite_count integer not null default 0,
    candidate_count integer not null default 0,
    max_confidence real not null default 1.0,
    representative_callsites text,  -- JSON array
    dispatch_kinds text,  -- JSON array
    resolution_kinds text,  -- JSON array
    refreshed_at integer,
    primary key (snapshot_id, caller_symbol_id, callee_symbol_id)
);

create index ix_call_rollup_caller on call_rollup(snapshot_id, caller_symbol_id);
create index ix_call_rollup_callee on call_rollup(snapshot_id, callee_symbol_id);

create table ref_rollup (
    snapshot_id integer not null,
    symbol_id integer not null,
    file_id integer not null,
    span text not null,  -- JSON object
    role_bits integer not null default 0,
    via_macro integer not null default 0,
    is_implicit integer not null default 0,
    context_count integer not null default 0,
    support_count integer not null default 0,
    max_confidence real not null default 0.0,
    refreshed_at integer,
    primary key (snapshot_id, symbol_id, file_id, span)
);

create index ix_ref_rollup_symbol on ref_rollup(snapshot_id, symbol_id);

create table impact_rollup (
    snapshot_id integer not null,
    owner_kind text not null check(owner_kind in ('symbol', 'file')),
    owner_id integer not null,
    incoming_refs integer not null default 0,
    outgoing_calls integer not null default 0,
    dependent_types integer not null default 0,
    dependent_headers integer not null default 0,
    refreshed_at integer,
    primary key (snapshot_id, owner_kind, owner_id)
);

create index ix_impact_rollup_snapshot_kind on impact_rollup(snapshot_id, owner_kind, owner_id);

create table file_overview (
    snapshot_id integer not null,
    file_id integer not null,
    path text not null,
    real_path text,
    language text not null,
    size_bytes integer not null default 0,
    line_count integer not null default 0,
    include_count integer not null default 0,
    import_count integer not null default 0,
    defined_symbol_count integer not null default 0,
    referenced_symbol_count integer not null default 0,
    top_external_dependencies text,  -- JSON array
    macro_event_count integer not null default 0,
    macro_density real not null default 0.0,
    parse_context_count integer not null default 0,
    parse_coverage real not null default 0.0,
    refreshed_at integer,
    primary key (snapshot_id, file_id)
);

create index ix_file_overview_snapshot_path on file_overview(snapshot_id, path);

-- Refresh functions (call from extraction service after initial extraction)
create view symbol_card_refresh as
 select * from symbol_card where snapshot_id = new_snapshot_id;

insert or replace into symbol_card (snapshot_id, symbol_id, stable_key, kind, name, qualified_name, visibility, template_kind, owner_symbol_id, owner_qualified_name, signature_text, decl_count, def_count, fwd_decl_count, top_callers, top_callees, top_refs, key_effects)
 select s.snapshot_id, s.stable_key, s.kind, s.name, s.qualified_name, s.visibility, s.template_kind, s.parent_symbol_id,
 owner.qualified_name as owner_qualified_name, s.canonical_decl_id, s.canonical_def_id,
 coalesce(ds.signature_text, '') as signature_text,
 coalesce(ds.decl_count, 0) as decl_count, coalesce(ds.def_count, 0) as def_count, coalesce(ds.fwd_decl_count, 0) as fwd_decl_count,
 (
        select json_group_array(json_object(
            'caller_symbol_id', ranked.caller_symbol_id,
            'callsite_count', ranked.callsite_count,
            'max_confidence', ranked.max_confidence
        ) order by ranked.callsite_count desc, ranked.caller_symbol_id
        limit 5
        ) as top_callers
        from (
            select cr.caller_symbol_id, cr.callsite_count, cr.max_confidence
            from call_rollup cr
            where cr.snapshot_id = new_snapshot_id and cr.callee_symbol_id = s.symbol_id
            order by cr.callsite_count desc, cr.caller_symbol_id
            limit 5
        ) as ranked
    ) as callers
    left join (
        select cr.callee_symbol_id, cr.callsite_count, cr.max_confidence
        from call_rollup cr
        where cr.snapshot_id = new_snapshot_id and cr.caller_symbol_id = s.symbol_id
        order by cr.callsite_count desc, cr.callee_symbol_id
        limit 5
        ) as ranked
    ) as top_callees
    left join (
        select rr.file_id, sum(rr.support_count) as support_count, max(rr.max_confidence) as max_confidence
        from ref_rollup rr
        where rr.snapshot_id = new_snapshot_id and rr.symbol_id = s.symbol_id
        group by rr.file_id
        order by support_count desc, rr.file_id
        limit 5
        ) as ranked
    ) as top_refs
    select json_group_array(json_object('effect_kind')) as key_effects
    from effect_summary
    where snapshot_id = new_snapshot_id and callable_symbol_id = s.symbol_id
    from symbol s
    where s.snapshot_id = new_snapshot_id;
