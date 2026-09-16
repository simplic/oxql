/*
 * OxQL language metadata for the Studio editor (contract 2):
 *   - JSON schema  → IntelliSense, hover hints and validation
 *   - snippets     → completion items for stages, operators, operands, variables
 *   - helpSections → content for the slide-in Help / cheat-sheet panel
 *
 * This file is loaded before app.js and exposes everything on window.OxQLLang.
 * It contains no dependency on Monaco so it can be evaluated immediately.
 */
(function () {
    "use strict";

    // ── JSON Schema ──────────────────────────────────────────────────────
    const DATE_TRUNC_UNITS = ["year", "quarter", "month", "week", "day", "hour", "minute", "second"];

    const condition = {
        type: "object",
        markdownDescription:
            "A field condition. Several operators on one path are combined with **and**.\n\n" +
            "Operands are written as the service returns the member: `long`/`decimal` as strings, " +
            "`dateTime` as ISO-8601 with `Z` or an offset, `date` as `YYYY-MM-DD`, `guid` as a string, " +
            "`enum` as a member name or number. `null` is legal for `eq`/`neq` on any member.",
        properties: {
            eq:         { markdownDescription: "**Equal to**. `null` means absent or null." },
            neq:        { markdownDescription: "**Not equal to**. `null` means present and non-null." },
            gt:         { markdownDescription: "**Greater than**." },
            gte:        { markdownDescription: "**Greater than or equal**." },
            lt:         { markdownDescription: "**Less than**." },
            lte:        { markdownDescription: "**Less than or equal**." },
            in:         { type: "array", markdownDescription: "**Value in array**, e.g. `[\"a\", \"b\"]`. `[]` matches nothing." },
            nin:        { type: "array", markdownDescription: "**Value not in array**. `[]` matches everything." },
            contains:   { type: "string", markdownDescription: "**String contains substring** (escaped by the engine)." },
            startsWith: { type: "string", markdownDescription: "**String starts with**." },
            endsWith:   { type: "string", markdownDescription: "**String ends with**." },
            exists:     { type: "boolean", markdownDescription: "**Field exists** (`true`/`false`)." },
            regex:      { type: "string", markdownDescription: "**Regular-expression match**, e.g. `\"^ABC.*\"`. Guarded: no nested quantifiers, no backreferences; unanchored patterns scan." },
            options: {
                type: "object",
                markdownDescription: "Condition options.",
                properties: {
                    ignoreCase: { type: "boolean", markdownDescription: "Case-insensitive comparison on `string` members for `eq neq in nin contains startsWith endsWith`." }
                },
                additionalProperties: false
            },
            any: {
                $ref: "#/definitions/match",
                markdownDescription: "**any** — evaluate the nested condition against **one element** of a collection of objects; inner paths are relative to the element."
            }
        },
        additionalProperties: false
    };

    const match = {
        type: "object",
        markdownDescription:
            "**match** — filter rows.\n\n" +
            "Field syntax: `{ \"field.path\": { \"op\": value } }` (wire names, camelCase, `id` at every depth).\n\n" +
            "Combine with logical groups `and` / `or` / `not`.",
        properties: {
            and: { type: "array", markdownDescription: "All conditions must match.", items: { $ref: "#/definitions/match" } },
            or:  { type: "array", markdownDescription: "Any condition may match.",   items: { $ref: "#/definitions/match" } },
            not: { $ref: "#/definitions/match", markdownDescription: "Negate the nested condition." }
        },
        additionalProperties: { $ref: "#/definitions/condition" }
    };

    const query = {
        type: "object",
        required: ["entityType"],
        markdownDescription: "An OxQL query: an `entityType` plus an ordered `pipeline` of stages.",
        properties: {
            entityType: {
                type: "string",
                markdownDescription: "**Required.** The schema's entity id, matched exactly, e.g. `vehicle.vehicle`."
            },
            variables: {
                type: "object",
                markdownDescription:
                    "Named values referenced in operands as `{ \"$var\": \"name\" }`, in the same encoding as a literal operand.\n\n" +
                    "```json\n{ \"variables\": { \"since\": \"2024-01-01T00:00:00Z\" } }\n```",
                additionalProperties: true
            },
            pipeline: {
                type: "array",
                markdownDescription:
                    "Ordered array of stages, executed as written.\n\n" +
                    "`match → lookup → resolve → unwind → group → project → sort → page`; `page` once, last.",
                items: { $ref: "#/definitions/stage" }
            }
        },
        additionalProperties: false
    };

    const schema = {
        $schema: "http://json-schema.org/draft-07/schema#",
        title: "OxQL Query",
        oneOf: [
            { $ref: "#/definitions/query" },
            { $ref: "#/definitions/batch" }
        ],
        definitions: {
            query: query,
            batch: {
                type: "object",
                required: ["queries"],
                markdownDescription: "A batch: several queries answered in order by `POST /batch`, always 200, each entry carrying its own outcome.",
                properties: {
                    queries:   { type: "array", items: { $ref: "#/definitions/query" }, markdownDescription: "The queries, up to the host's `maxBatchQueries`." },
                    maxTimeMs: { type: "number", markdownDescription: "A ceiling on every query's aggregate, under the host's." }
                },
                additionalProperties: false
            },
            stage: {
                type: "object",
                markdownDescription: "A pipeline stage. Use **exactly one** of the stage keys.",
                properties: {
                    match:   { $ref: "#/definitions/match" },
                    lookup:  { $ref: "#/definitions/lookup" },
                    resolve: { $ref: "#/definitions/resolve" },
                    unwind:  { $ref: "#/definitions/unwind" },
                    group:   { $ref: "#/definitions/group" },
                    project: { $ref: "#/definitions/project" },
                    sort:    { $ref: "#/definitions/sort" },
                    page:    { $ref: "#/definitions/page" }
                },
                additionalProperties: false
            },
            match: match,
            condition: condition,
            lookup: {
                type: "object",
                markdownDescription:
                    "**lookup** — backward join along a declared reference: `from` is the child entity, `path` the child's member " +
                    "that references the current entity; the children arrive as an array under `as`, ordered by their key.",
                required: ["from", "path", "as"],
                properties: {
                    from:   { type: "string", markdownDescription: "The child entity id (local to this host)." },
                    path:   { type: "string", markdownDescription: "The child's member carrying the declared reference to the current entity." },
                    as:     { type: "string", markdownDescription: "Alias the array is placed under; a plain identifier that collides with no member." },
                    select: { type: "array", items: { type: "string" }, markdownDescription: "Wire paths on the child to keep; `id` is always kept; the key and display members by default." },
                    filter: { $ref: "#/definitions/match", markdownDescription: "A condition on the child." },
                    limit:  { type: "number", markdownDescription: "The most children per parent, under the host cap." }
                },
                additionalProperties: false
            },
            resolve: {
                type: "object",
                markdownDescription:
                    "**resolve** — forward join along a declared reference: `path` carries the reference, the target row is placed under `as` " +
                    "(or `null`). A local target is filterable and sortable; a remote target is filterable as a semi-join and not sortable.",
                required: ["path", "as"],
                properties: {
                    path:   { type: "string", markdownDescription: "The member carrying the declared reference." },
                    as:     { type: "string", markdownDescription: "Alias the object is placed under." },
                    select: { type: "array", items: { type: "string" }, markdownDescription: "Wire paths on the target to keep." },
                    filter: { $ref: "#/definitions/match", markdownDescription: "A condition on the target; a non-match yields `null`." }
                },
                additionalProperties: false
            },
            unwind: {
                type: "object",
                markdownDescription: "**unwind** — one row per element of a collection at the current shape; the path then means the element.",
                required: ["path"],
                properties: {
                    path:         { type: "string",  markdownDescription: "Wire path of the collection. An inner collection needs its outer unwound first." },
                    as:           { type: "string",  markdownDescription: "Alias for the element (a copy)." },
                    preserveNull: { type: "boolean", markdownDescription: "Keep rows where the collection is null or empty. Default `false`." },
                    includeIndex: { type: "string",  markdownDescription: "Alias for the element's index (`int`)." }
                },
                additionalProperties: false
            },
            group: {
                type: "object",
                markdownDescription: "**group** — keys and aggregates replace the shape; later stages see the aliases only. Pages continue by offset behind the cursor.",
                properties: {
                    by: {
                        type: "array",
                        markdownDescription: "Group keys. Each entry is a scalar `path` or a `dateTrunc`, with an `as` alias.",
                        items: {
                            type: "object",
                            properties: {
                                path: { type: "string", markdownDescription: "Scalar wire path to group by (not under a collection)." },
                                as:   { type: "string", markdownDescription: "Output alias." },
                                dateTrunc: {
                                    type: "object",
                                    markdownDescription: "Truncate a date to a unit; the key is the UTC instant of the local boundary.",
                                    properties: {
                                        path:      { type: "string" },
                                        unit:      { enum: DATE_TRUNC_UNITS },
                                        timezone:  { type: "string", markdownDescription: "IANA timezone, default `UTC`." },
                                        weekStart: { type: "string", markdownDescription: "First day of the week for `unit: week`, default `monday`." }
                                    }
                                }
                            }
                        }
                    },
                    fields: {
                        type: "object",
                        markdownDescription:
                            "Aggregates keyed by alias.\n\n" +
                            "Functions: `count` `countDistinct` `sum` `avg` `min` `max` `first` `last` `push`; `sum`/`avg` need a numeric argument.",
                        additionalProperties: true
                    }
                },
                additionalProperties: false
            },
            project: {
                type: "object",
                markdownDescription:
                    "**project** — inclusion (`1`) or exclusion (`0`), never both. `id` is kept by default in an inclusion and can be excluded explicitly.\n\n" +
                    "Flat dot-notation and nested objects are equivalent:\n" +
                    "```json\n{ \"status\": { \"name\": 1 } }\n```",
                additionalProperties: true
            },
            sort: {
                type: "array",
                markdownDescription:
                    "**sort** — array of one-property objects mapping a scalar wire path → direction. Paths in collections are not sortable.\n\n" +
                    "On a root shape the engine appends the `id` tie-breaker.",
                items: {
                    type: "object",
                    additionalProperties: { enum: ["asc", "desc"] }
                }
            },
            page: {
                type: "object",
                markdownDescription: "**page** — once, last. Continue with the previous response's `nextCursor`, or jump with `offset` below the host's `maxOffset`.",
                properties: {
                    limit:             { type: "number",           markdownDescription: "Rows per page; the host default when absent, the host maximum above." },
                    cursor:            { type: ["string", "null"], markdownDescription: "Opaque signed token from a previous response's `nextCursor`; bound to this query." },
                    offset:            { type: "number",           markdownDescription: "Rows to skip, up to the host's `maxOffset`; not with a cursor." },
                    includeTotalCount: { type: "boolean",          markdownDescription: "Count the matching rows beside the page, up to `countCap` (`totalCountCapped` says when). Default `false`." }
                },
                additionalProperties: false
            }
        }
    };

    // ── Snippets (Monaco-agnostic; app.js wraps them into CompletionItems) ─
    // kind ∈ stage | logical | operator | operand | value | function

    const stageSnippets = [
        {
            label: "match", kind: "stage", detail: "Filter rows",
            documentation: "Filter rows by field conditions and logical groups.",
            insertText: '{\n  "match": {\n    "${1:field.path}": { "${2:eq}": ${3:value} }\n  }\n}'
        },
        {
            label: "lookup", kind: "stage", detail: "Backward join along a declared reference",
            documentation: "The child entity's rows that reference the current row, as an array under `as`.",
            insertText: '{\n  "lookup": {\n    "from": "${1:logistics.shipment_document}",\n    "path": "${2:shipmentId}",\n    "as": "${3:documents}",\n    "select": ["${4:id}"],\n    "limit": ${5:10}\n  }\n}'
        },
        {
            label: "resolve", kind: "stage", detail: "Forward join along a declared reference",
            documentation: "The target row the member references, as an object (or null) under `as`.",
            insertText: '{\n  "resolve": {\n    "path": "${1:vehicleId}",\n    "as": "${2:vehicle}",\n    "select": ["${3:id}", "${4:matchCode}"]\n  }\n}'
        },
        {
            label: "unwind", kind: "stage", detail: "One row per element of a collection",
            documentation: "The path then means the element; `as` keeps a copy, `includeIndex` the index.",
            insertText: '{\n  "unwind": {\n    "path": "${1:appointments}",\n    "as": "${2:appointment}",\n    "preserveNull": ${3:false}\n  }\n}'
        },
        {
            label: "group", kind: "stage", detail: "Group and aggregate",
            documentation: "Keys and aggregates replace the shape.",
            insertText: '{\n  "group": {\n    "by": [ { "path": "${1:status.name}", "as": "${2:status}" } ],\n    "fields": {\n      "${3:total}": { "count": true }\n    }\n  }\n}'
        },
        {
            label: "project", kind: "stage", detail: "Inclusion or exclusion",
            documentation: "Include (1) or exclude (0) paths; never both. Nested objects supported.",
            insertText: '{\n  "project": {\n    "id": 1,\n    "${1:matchCode}": 1\n  }\n}'
        },
        {
            label: "sort", kind: "stage", detail: "Order rows",
            documentation: "Order rows by scalar paths (asc/desc).",
            insertText: '{\n  "sort": [\n    { "${1:createDateTime}": "${2:desc}" }\n  ]\n}'
        },
        {
            label: "page", kind: "stage", detail: "Paging: cursor or offset",
            documentation: "Once, last. Continue by cursor or jump by offset.",
            insertText: '{\n  "page": {\n    "limit": ${1:50},\n    "includeTotalCount": ${2:false}\n  }\n}'
        }
    ];

    const logicalSnippets = [
        {
            label: "and", kind: "logical", detail: "Logical AND group",
            documentation: "All nested conditions must match.",
            insertText: '"and": [\n  { "${1:field}": { "${2:eq}": ${3:value} } },\n  { "${4:field}": { "${5:eq}": ${6:value} } }\n]'
        },
        {
            label: "or", kind: "logical", detail: "Logical OR group",
            documentation: "Any nested condition may match.",
            insertText: '"or": [\n  { "${1:field}": { "${2:eq}": ${3:value} } },\n  { "${4:field}": { "${5:eq}": ${6:value} } }\n]'
        },
        {
            label: "not", kind: "logical", detail: "Logical NOT",
            documentation: "Negate the nested condition.",
            insertText: '"not": { "${1:field}": { "${2:eq}": ${3:value} } }'
        },
        {
            label: "any", kind: "logical", detail: "One element of a collection",
            documentation: "Evaluate the nested condition against one element of a collection of objects; inner paths relative to the element.",
            insertText: '"${1:items}": { "any": { "${2:quantity}": { "${3:gt}": ${4:1} } } }'
        }
    ];

    const operatorSnippets = [
        { label: "eq",         kind: "operator", detail: "Equal to",                 insertText: '"eq": ${1:value}' },
        { label: "neq",        kind: "operator", detail: "Not equal to",             insertText: '"neq": ${1:value}' },
        { label: "gt",         kind: "operator", detail: "Greater than",             insertText: '"gt": ${1:value}' },
        { label: "gte",        kind: "operator", detail: "Greater than or equal",    insertText: '"gte": ${1:value}' },
        { label: "lt",         kind: "operator", detail: "Less than",                insertText: '"lt": ${1:value}' },
        { label: "lte",        kind: "operator", detail: "Less than or equal",       insertText: '"lte": ${1:value}' },
        { label: "in",         kind: "operator", detail: "Value in array",           insertText: '"in": [${1:values}]' },
        { label: "nin",        kind: "operator", detail: "Value not in array",       insertText: '"nin": [${1:values}]' },
        { label: "contains",   kind: "operator", detail: "String contains",          insertText: '"contains": "${1:text}"' },
        { label: "startsWith", kind: "operator", detail: "String starts with",       insertText: '"startsWith": "${1:prefix}"' },
        { label: "endsWith",   kind: "operator", detail: "String ends with",         insertText: '"endsWith": "${1:suffix}"' },
        { label: "exists",     kind: "operator", detail: "Field exists",             insertText: '"exists": ${1:true}' },
        { label: "regex",      kind: "operator", detail: "Regular-expression match", insertText: '"regex": "${1:^ABC.*}"' },
        { label: "ignoreCase", kind: "operator", detail: "Case-insensitive option",  insertText: '"options": { "ignoreCase": true }' }
    ];

    const operandSnippets = [
        { label: "dateTime", kind: "operand", detail: "ISO-8601 with Z or an offset",     insertText: '"${1:2024-01-15T10:30:00Z}"' },
        { label: "date",     kind: "operand", detail: "YYYY-MM-DD",                       insertText: '"${1:2024-01-15}"' },
        { label: "guid",     kind: "operand", detail: "GUID string",                      insertText: '"${1:00000000-0000-0000-0000-000000000000}"' },
        { label: "long",     kind: "operand", detail: "Number or string",                 insertText: '"${1:9007199254740993}"' },
        { label: "decimal",  kind: "operand", detail: "Number or string",                 insertText: '"${1:19.99}"' },
        { label: "enum",     kind: "operand", detail: "Member name or number",            insertText: '"${1:Active}"' },
        { label: "timeSpan", kind: "operand", detail: "ISO-8601 duration",                insertText: '"${1:PT1H30M}"' },
        { label: "null",     kind: "operand", detail: "Absent or null (eq), present and non-null (neq)", insertText: 'null' }
    ];

    const valueSnippets = [
        { label: "$var", kind: "value", detail: "Variable reference", documentation: "Inject a value from the top-level `variables` object, typed at its use.", insertText: '{ "$var": "${1:name}" }' }
    ];

    const aggregationSnippets = [
        { label: "count",         kind: "function", detail: "Count of rows",        insertText: '"${1:total}": { "count": true }' },
        { label: "countDistinct", kind: "function", detail: "Count distinct",       insertText: '"${1:distinct}": { "countDistinct": { "path": "${2:field}" } }' },
        { label: "sum",           kind: "function", detail: "Sum of a numeric path", insertText: '"${1:sum}": { "sum": { "path": "${2:field}" } }' },
        { label: "avg",           kind: "function", detail: "Average of a numeric path", insertText: '"${1:average}": { "avg": { "path": "${2:field}" } }' },
        { label: "min",           kind: "function", detail: "Minimum value",        insertText: '"${1:min}": { "min": { "path": "${2:field}" } }' },
        { label: "max",           kind: "function", detail: "Maximum value",        insertText: '"${1:max}": { "max": { "path": "${2:field}" } }' },
        { label: "push",          kind: "function", detail: "Array of all values",  insertText: '"${1:items}": { "push": { "path": "${2:field}" } }' },
        { label: "dateTrunc",     kind: "function", detail: "Truncated date key",   insertText: '{ "dateTrunc": { "path": "${1:createDateTime}", "unit": "${2:week}", "timezone": "${3:Europe/Berlin}" }, "as": "${4:week}" }' }
    ];

    function allSnippets() {
        return [].concat(
            stageSnippets, logicalSnippets, operatorSnippets,
            operandSnippets, valueSnippets, aggregationSnippets
        );
    }

    // ── Help / cheat-sheet content ───────────────────────────────────────
    // Each item: { name, desc, insert? }  — insert is the example inserted at the cursor.
    const helpSections = [
        {
            title: "Query structure",
            items: [
                { name: "entityType", desc: "Required. The schema's entity id, matched exactly (see the explorer)." },
                { name: "paths", desc: "Wire names, camelCase, `id` at every depth; storage spelling is refused under contract 2." },
                { name: "variables", desc: "Named values reused in operands via { \"$var\": \"name\" }, typed at their use." },
                { name: "pipeline", desc: "Ordered stages: match → lookup → resolve → unwind → group → project → sort → page." },
                {
                    name: "New query skeleton", desc: "Insert a minimal starter query.",
                    insert: '{\n  "entityType": "vehicle.vehicle",\n  "variables": {},\n  "pipeline": [\n    { "match": {} },\n    { "sort": [ { "id": "asc" } ] },\n    { "page": { "limit": 25 } }\n  ]\n}'
                },
                {
                    name: "Batch", desc: "A document with `queries` is sent to POST /batch; every entry answers on its own.",
                    insert: '{\n  "maxTimeMs": 5000,\n  "queries": [\n    { "entityType": "vehicle.vehicle", "pipeline": [ { "page": { "limit": 25, "includeTotalCount": true } } ] },\n    { "entityType": "vehicle.vehicle", "pipeline": [ { "group": { "by": [ { "path": "status.name", "as": "status" } ], "fields": { "n": { "count": true } } } } ] }\n  ]\n}'
                }
            ]
        },
        {
            title: "Pipeline stages",
            items: stageSnippets.map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Filter operators",
            items: operatorSnippets.map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Logical groups",
            items: logicalSnippets.map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Operand encoding",
            items: operandSnippets.map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Variables & aggregations",
            items: [].concat(valueSnippets, aggregationSnippets)
                     .map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Response",
            items: [
                { name: "items", desc: "Rows in the wire encoding at every depth; shaped rows follow the fold (aliases, group outputs)." },
                { name: "pageInfo", desc: "hasNextPage, nextCursor, and totalCount / totalCountCapped when a count was requested." },
                { name: "diagnostics", desc: "What did not change the rows (ENTITY_ID_RETIRED, TOTAL_COUNT_CAPPED, SORT_ON_ADDON, REGEX_UNANCHORED, …)." },
                { name: "refusal", desc: "{ type, title, errors: [{ code, message, stage, path }] }; 400 caller error, 403 unscoped, 413 too large, 422 not executable here, 504 timeout." }
            ]
        }
    ];

    window.OxQLLang = {
        schema,
        stageSnippets,
        logicalSnippets,
        operatorSnippets,
        operandSnippets,
        valueSnippets,
        aggregationSnippets,
        allSnippets,
        helpSections
    };
})();
