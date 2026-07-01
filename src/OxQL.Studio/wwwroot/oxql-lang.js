/*
 * OxQL language metadata for the Studio editor:
 *   - JSON schema  → IntelliSense, hover hints and validation
 *   - snippets     → completion items for stages, operators, type hints, variables
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
        markdownDescription: "A field condition. Use one or more operators.",
        properties: {
            eq:         { markdownDescription: "**Equal to**." },
            neq:        { markdownDescription: "**Not equal to**." },
            gt:         { markdownDescription: "**Greater than**." },
            gte:        { markdownDescription: "**Greater than or equal**." },
            lt:         { markdownDescription: "**Less than**." },
            lte:        { markdownDescription: "**Less than or equal**." },
            in:         { markdownDescription: "**Value in array**, e.g. `[\"a\", \"b\"]`." },
            nin:        { markdownDescription: "**Value not in array**." },
            contains:   { markdownDescription: "**String contains substring**." },
            startsWith: { markdownDescription: "**String starts with**." },
            endsWith:   { markdownDescription: "**String ends with**." },
            exists:     { type: "boolean", markdownDescription: "**Field exists** (`true`/`false`)." },
            regex:      { markdownDescription: "**Regular-expression match**, e.g. `\"^ABC.*\"`." }
        },
        additionalProperties: false
    };

    const match = {
        type: "object",
        markdownDescription:
            "**match** — filter documents.\n\n" +
            "Field syntax: `{ \"Field.Path\": { \"op\": value } }`.\n\n" +
            "Combine with logical groups `and` / `or` / `not`.",
        properties: {
            and: { type: "array", markdownDescription: "All conditions must match.", items: { $ref: "#/definitions/match" } },
            or:  { type: "array", markdownDescription: "Any condition may match.",   items: { $ref: "#/definitions/match" } },
            not: { $ref: "#/definitions/match", markdownDescription: "Negate the nested condition." }
        },
        additionalProperties: { $ref: "#/definitions/condition" }
    };

    const schema = {
        $schema: "http://json-schema.org/draft-07/schema#",
        title: "OxQL Query",
        type: "object",
        required: ["entityType"],
        markdownDescription: "An OxQL query: an `entityType` plus an ordered `pipeline` of stages.",
        properties: {
            entityType: {
                type: "string",
                markdownDescription: "**Required.** The collection / entity to query, e.g. `vehicle.vehicle`."
            },
            variables: {
                type: "object",
                markdownDescription:
                    "Named variables referenced in filters as `{ \"$var\": \"name\" }`.\n\n" +
                    "```json\n{ \"variables\": { \"orgId\": \"7feec12f-...\" } }\n```",
                additionalProperties: true
            },
            pipeline: {
                type: "array",
                markdownDescription:
                    "Ordered array of stages, executed top to bottom.\n\n" +
                    "Recommended order: `match → lookup → resolve → unwind → group → project → sort → page`.",
                items: { $ref: "#/definitions/stage" }
            }
        },
        additionalProperties: false,
        definitions: {
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
                markdownDescription: "**lookup** — left-join a related collection; joined docs are embedded as an array under `as`.",
                required: ["from", "localPath", "foreignPath", "as"],
                properties: {
                    from:        { type: "string", markdownDescription: "Target collection name (must be server-allowed)." },
                    localPath:   { type: "string", markdownDescription: "Field in the current document holding the foreign key." },
                    foreignPath: { type: "string", markdownDescription: "Field in the target collection to match (usually `id`)." },
                    as:          { type: "string", markdownDescription: "Alias under which the joined result is embedded." }
                },
                additionalProperties: false
            },
            resolve: {
                type: "object",
                markdownDescription: "**resolve** — fetch a related document from a configured external source.",
                required: ["source", "localPath", "as"],
                properties: {
                    source:    { type: "string", markdownDescription: "External source id (must be server-allowed), e.g. `crm.customer`." },
                    localPath: { type: "string", markdownDescription: "Local field holding the lookup key." },
                    as:        { type: "string", markdownDescription: "Alias for the resolved result." }
                },
                additionalProperties: false
            },
            unwind: {
                type: "object",
                markdownDescription: "**unwind** — deconstruct an array field into one document per element.",
                required: ["path"],
                properties: {
                    path:         { type: "string",  markdownDescription: "Dot-notation path to the array field." },
                    as:           { type: "string",  markdownDescription: "Alias for the unwound element." },
                    preserveNull: { type: "boolean", markdownDescription: "Keep docs where the array is null/empty. Default `false`." },
                    includeIndex: { type: "string",  markdownDescription: "Output field to store the element index." }
                },
                additionalProperties: false
            },
            group: {
                type: "object",
                markdownDescription: "**group** — aggregate documents into groups and compute aggregate values.",
                properties: {
                    by: {
                        type: "array",
                        markdownDescription: "Group-by keys. Each entry is a `path` or a `dateTrunc`.",
                        items: {
                            type: "object",
                            properties: {
                                path: { type: "string", markdownDescription: "Field path to group by." },
                                as:   { type: "string", markdownDescription: "Output key name." },
                                dateTrunc: {
                                    type: "object",
                                    markdownDescription: "Truncate a date field to a unit before grouping.",
                                    properties: {
                                        path: { type: "string" },
                                        unit: { enum: DATE_TRUNC_UNITS }
                                    }
                                }
                            }
                        }
                    },
                    fields: {
                        type: "object",
                        markdownDescription:
                            "Aggregation expressions keyed by output name.\n\n" +
                            "Functions: `count` `countDistinct` `sum` `avg` `min` `max` `first` `last` `push`.",
                        additionalProperties: true
                    }
                },
                additionalProperties: false
            },
            project: {
                type: "object",
                markdownDescription:
                    "**project** — include (`1`) or exclude (`0`) fields.\n\n" +
                    "Flat dot-notation and nested objects are equivalent:\n" +
                    "```json\n{ \"Status\": { \"Name\": 1 } }\n```",
                additionalProperties: true
            },
            sort: {
                type: "array",
                markdownDescription:
                    "**sort** — array of one-property objects mapping field → direction.\n\n" +
                    "A tie-breaker sort on `id` is appended automatically.",
                items: {
                    type: "object",
                    additionalProperties: { enum: ["asc", "desc"] }
                }
            },
            page: {
                type: "object",
                markdownDescription: "**page** — result size and cursor-based forward pagination.",
                properties: {
                    limit:             { type: "number",           markdownDescription: "Max documents to return. Default `50` (server-capped)." },
                    cursor:            { type: ["string", "null"], markdownDescription: "Opaque token from a previous response's `nextCursor`." },
                    includeTotalCount: { type: "boolean",          markdownDescription: "Include total matching count in the response. Default `false`." }
                },
                additionalProperties: false
            }
        }
    };

    // ── Snippets (Monaco-agnostic; app.js wraps them into CompletionItems) ─
    // kind ∈ stage | logical | operator | typehint | value | function

    const stageSnippets = [
        {
            label: "match", kind: "stage", detail: "Filter documents",
            documentation: "Filter documents by field conditions and logical groups.",
            insertText: '{\n  "match": {\n    "${1:Field.Path}": { "${2:eq}": ${3:value} }\n  }\n}'
        },
        {
            label: "lookup", kind: "stage", detail: "Join a related collection",
            documentation: "Left-join a related collection; joined docs embedded under `as`.",
            insertText: '{\n  "lookup": {\n    "from": "${1:customers}",\n    "localPath": "${2:attributes.customerId}",\n    "foreignPath": "${3:id}",\n    "as": "${4:customer}"\n  }\n}'
        },
        {
            label: "resolve", kind: "stage", detail: "Fetch from an external source",
            documentation: "Fetch a related document from a configured external source.",
            insertText: '{\n  "resolve": {\n    "source": "${1:crm.customer}",\n    "localPath": "${2:attributes.customerId}",\n    "as": "${3:crmCustomer}"\n  }\n}'
        },
        {
            label: "unwind", kind: "stage", detail: "Deconstruct an array field",
            documentation: "Emit one document per element of an array field.",
            insertText: '{\n  "unwind": {\n    "path": "${1:Appointments}",\n    "as": "${2:appointment}",\n    "preserveNull": ${3:false}\n  }\n}'
        },
        {
            label: "group", kind: "stage", detail: "Aggregate / group results",
            documentation: "Group documents and compute aggregate values.",
            insertText: '{\n  "group": {\n    "by": [ { "path": "${1:Status.Name}", "as": "${2:status}" } ],\n    "fields": {\n      "${3:total}": { "count": true }\n    }\n  }\n}'
        },
        {
            label: "project", kind: "stage", detail: "Select / exclude output fields",
            documentation: "Include (1) or exclude (0) fields. Nested objects supported.",
            insertText: '{\n  "project": {\n    "id": 1,\n    "${1:MatchCode}": 1\n  }\n}'
        },
        {
            label: "sort", kind: "stage", detail: "Order results",
            documentation: "Order results by one or more fields (asc/desc).",
            insertText: '{\n  "sort": [\n    { "${1:CreatedAt}": "${2:desc}" }\n  ]\n}'
        },
        {
            label: "page", kind: "stage", detail: "Cursor-based pagination",
            documentation: "Control result size and forward pagination.",
            insertText: '{\n  "page": {\n    "limit": ${1:50},\n    "cursor": ${2:null},\n    "includeTotalCount": ${3:false}\n  }\n}'
        }
    ];

    const logicalSnippets = [
        {
            label: "and", kind: "logical", detail: "Logical AND group",
            documentation: "All nested conditions must match.",
            insertText: '"and": [\n  { "${1:Field}": { "${2:eq}": ${3:value} } },\n  { "${4:Field}": { "${5:eq}": ${6:value} } }\n]'
        },
        {
            label: "or", kind: "logical", detail: "Logical OR group",
            documentation: "Any nested condition may match.",
            insertText: '"or": [\n  { "${1:Field}": { "${2:eq}": ${3:value} } },\n  { "${4:Field}": { "${5:eq}": ${6:value} } }\n]'
        },
        {
            label: "not", kind: "logical", detail: "Logical NOT",
            documentation: "Negate the nested condition.",
            insertText: '"not": { "${1:Field}": { "${2:eq}": ${3:value} } }'
        }
    ];

    const operatorSnippets = [
        { label: "eq",         kind: "operator", detail: "Equal to",               insertText: '"eq": ${1:value}' },
        { label: "neq",        kind: "operator", detail: "Not equal to",           insertText: '"neq": ${1:value}' },
        { label: "gt",         kind: "operator", detail: "Greater than",           insertText: '"gt": ${1:value}' },
        { label: "gte",        kind: "operator", detail: "Greater than or equal",  insertText: '"gte": ${1:value}' },
        { label: "lt",         kind: "operator", detail: "Less than",              insertText: '"lt": ${1:value}' },
        { label: "lte",        kind: "operator", detail: "Less than or equal",     insertText: '"lte": ${1:value}' },
        { label: "in",         kind: "operator", detail: "Value in array",         insertText: '"in": [${1:values}]' },
        { label: "nin",        kind: "operator", detail: "Value not in array",     insertText: '"nin": [${1:values}]' },
        { label: "contains",   kind: "operator", detail: "String contains",        insertText: '"contains": "${1:text}"' },
        { label: "startsWith", kind: "operator", detail: "String starts with",     insertText: '"startsWith": "${1:prefix}"' },
        { label: "endsWith",   kind: "operator", detail: "String ends with",       insertText: '"endsWith": "${1:suffix}"' },
        { label: "exists",     kind: "operator", detail: "Field exists",           insertText: '"exists": ${1:true}' },
        { label: "regex",      kind: "operator", detail: "Regular-expression match", insertText: '"regex": "${1:^ABC.*}"' }
    ];

    const typeHintSnippets = [
        { label: "$uuid",    kind: "typehint", detail: "Binary subtype 4 — RFC UUID",   insertText: '{ "$uuid": "${1:00000000-0000-0000-0000-000000000000}" }' },
        { label: "$uuid3",   kind: "typehint", detail: "Binary subtype 3 — C# legacy",  insertText: '{ "$uuid3": "${1:00000000-0000-0000-0000-000000000000}" }' },
        { label: "$date",    kind: "typehint", detail: "DateTime (UTC)",                insertText: '{ "$date": "${1:2024-01-15T10:30:00Z}" }' },
        { label: "$oid",     kind: "typehint", detail: "ObjectId",                      insertText: '{ "$oid": "${1:507f1f77bcf86cd799439011}" }' },
        { label: "$long",    kind: "typehint", detail: "Int64",                         insertText: '{ "$long": "${1:9007199254740993}" }' },
        { label: "$decimal", kind: "typehint", detail: "Decimal128",                    insertText: '{ "$decimal": "${1:19.99}" }' },
        { label: "$regex",   kind: "typehint", detail: "RegularExpression",             insertText: '{ "$regex": "${1:^abc}" }' },
        { label: "$null",    kind: "typehint", detail: "Null value",                    insertText: '{ "$null": true }' }
    ];

    const valueSnippets = [
        { label: "$var", kind: "value", detail: "Variable reference", documentation: "Inject a value from the top-level `variables` object.", insertText: '{ "$var": "${1:name}" }' }
    ];

    const aggregationSnippets = [
        { label: "count",         kind: "function", detail: "Count of documents",  insertText: '"${1:total}": { "count": true }' },
        { label: "countDistinct", kind: "function", detail: "Count distinct",      insertText: '"${1:distinct}": { "countDistinct": "${2:field}" }' },
        { label: "sum",           kind: "function", detail: "Sum of a field",      insertText: '"${1:sum}": { "sum": { "path": "${2:field}" } }' },
        { label: "avg",           kind: "function", detail: "Average of a field",  insertText: '"${1:average}": { "avg": { "path": "${2:field}" } }' },
        { label: "min",           kind: "function", detail: "Minimum value",       insertText: '"${1:min}": { "min": { "path": "${2:field}" } }' },
        { label: "max",           kind: "function", detail: "Maximum value",       insertText: '"${1:max}": { "max": { "path": "${2:field}" } }' },
        { label: "push",          kind: "function", detail: "Array of all values", insertText: '"${1:items}": { "push": { "path": "${2:field}" } }' }
    ];

    function allSnippets() {
        return [].concat(
            stageSnippets, logicalSnippets, operatorSnippets,
            typeHintSnippets, valueSnippets, aggregationSnippets
        );
    }

    // ── Help / cheat-sheet content ───────────────────────────────────────
    // Each item: { name, desc, insert? }  — insert is the example inserted at the cursor.
    const helpSections = [
        {
            title: "Query structure",
            items: [
                { name: "entityType", desc: "Required. The entity / collection to query." },
                { name: "variables", desc: "Named values reused in filters via { \"$var\": \"name\" }." },
                { name: "pipeline", desc: "Ordered stages: match → lookup → resolve → unwind → group → project → sort → page." },
                {
                    name: "New query skeleton", desc: "Insert a minimal starter query.",
                    insert: '{\n  "entityType": "vehicle.vehicle",\n  "variables": {},\n  "pipeline": [\n    { "match": {} },\n    { "sort": [ { "createdAt": "desc" } ] },\n    { "page": { "limit": 25 } }\n  ]\n}'
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
            title: "Type hints",
            items: typeHintSnippets.map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        },
        {
            title: "Variables & aggregations",
            items: [].concat(valueSnippets, aggregationSnippets)
                     .map(s => ({ name: s.label, desc: s.detail, snippet: s.insertText }))
        }
    ];

    window.OxQLLang = {
        schema,
        stageSnippets,
        logicalSnippets,
        operatorSnippets,
        typeHintSnippets,
        valueSnippets,
        aggregationSnippets,
        allSnippets,
        helpSections
    };
})();
