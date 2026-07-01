# OxQL Query Syntax Reference

OxQL is a JSON-based document query language. A query is a JSON object with an `entityType` and an ordered `pipeline` of stages. Each stage transforms or filters the result set.

---

## Top-level structure

```json
{
  "entityType": "string",
  "variables": { "name": value },
  "pipeline": [ ...stages ]
}
```

| Field | Required | Description |
|---|---|---|
| `entityType` | ✅ | The collection / entity to query (e.g. `"vehicle"`). |
| `variables` | ❌ | Named variables referenced in filters as `{ "$var": "name" }`. |
| `pipeline` | ✅ | Ordered array of stage objects. Executed top to bottom. |

---

## Pipeline stages

Each element in `pipeline` is an object with **one** of the following keys:

| Key | Purpose |
|---|---|
| `match` | Filter documents |
| `lookup` | Join a related collection |
| `resolve` | Fetch from an external source |
| `unwind` | Deconstruct an array field |
| `group` | Aggregate / group results |
| `project` | Select / exclude output fields |
| `sort` | Order results |
| `page` | Cursor-based pagination |

Recommended stage order: `match → lookup → resolve → unwind → group → project → sort → page`

---

## Stage: `match`

Filters documents. Supports single conditions, logical groups, and nesting.

### Single condition

```json
{ "match": { "FieldPath": { "op": value } } }
```

### Logical AND

```json
{
  "match": {
    "and": [
      { "Status.Name": { "eq": "active" } },
      { "OrganizationId": { "eq": "7feec12f-870f-4087-a676-27e411d570a8" } }
    ]
  }
}
```

### Logical OR

```json
{
  "match": {
    "or": [
      { "Status.Name": { "eq": "active" } },
      { "Status.Name": { "eq": "pending" } }
    ]
  }
}
```

### Logical NOT

```json
{
  "match": {
    "not": { "Status.Name": { "eq": "deleted" } }
  }
}
```

### Nested logical groups

`and` / `or` / `not` can be nested arbitrarily.

```json
{
  "match": {
    "and": [
      { "OrganizationId": { "eq": "7feec12f-..." } },
      {
        "or": [
          { "Status.Name": { "eq": "active" } },
          { "Status.Name": { "eq": "pending" } }
        ]
      }
    ]
  }
}
```

### Filter operators

| Operator | Description | Example value |
|---|---|---|
| `eq` | Equal | `"active"` |
| `neq` | Not equal | `"deleted"` |
| `gt` | Greater than | `100` |
| `gte` | Greater than or equal | `100` |
| `lt` | Less than | `100` |
| `lte` | Less than or equal | `100` |
| `in` | Value in array | `["a", "b"]` |
| `nin` | Value not in array | `["x", "y"]` |
| `contains` | String contains substring | `"abc"` |
| `startsWith` | String starts with | `"pre"` |
| `endsWith` | String ends with | `"fix"` |
| `exists` | Field exists | `true` / `false` |
| `regex` | Regular expression match | `"^ABC.*"` |

### Variable references

Variables declared in the top-level `variables` object can be injected into filter values:

```json
{
  "variables": { "orgId": "7feec12f-..." },
  "pipeline": [
    { "match": { "OrganizationId": { "eq": { "$var": "orgId" } } } }
  ]
}
```

### Type hints

By default string values are matched as strings. To force an exact BSON type, wrap the value in a type hint object:

| Hint | Example | BSON type produced |
|---|---|---|
| *(plain string)* | `"abc"` | `String` (auto-detects GUID, tries all UUID subtypes) |
| `{ "$uuid": "..." }` | `{ "$uuid": "7feec12f-..." }` | `Binary` subtype 4 — RFC standard UUID |
| `{ "$uuid3": "..." }` | `{ "$uuid3": "7feec12f-..." }` | `Binary` subtype 3 — C# legacy UUID |
| `{ "$date": "..." }` | `{ "$date": "2024-01-15T10:30:00Z" }` | `DateTime` UTC |
| `{ "$oid": "..." }` | `{ "$oid": "507f1f77bcf86cd799439011" }` | `ObjectId` |
| `{ "$long": "..." }` | `{ "$long": "9007199254740993" }` | `Int64` |
| `{ "$decimal": "..." }` | `{ "$decimal": "19.99" }` | `Decimal128` |
| `{ "$regex": "..." }` | `{ "$regex": "^abc" }` | `RegularExpression` |
| `{ "$null": true }` | `{ "$null": true }` | `Null` |

> **GUID tip:** Passing a plain GUID string automatically emits an `$or` that matches binary subtype 3, subtype 4, and plain string — covering any storage format. Use `$uuid` / `$uuid3` only when you need to target one specific binary subtype.

```json
{ "CreatedAt": { "gte": { "$date": "2024-01-01T00:00:00Z" } } }
{ "Price":     { "eq":  { "$decimal": "19.99" } } }
{ "LegacyId":  { "eq":  { "$uuid3": "7feec12f-..." } } }
```

---

## Stage: `lookup`

Joins a related collection (left join). The joined documents are embedded as an array under the alias.

```json
{
  "lookup": {
    "from": "customers",
    "localPath": "attributes.customerId",
    "foreignPath": "id",
    "as": "customer"
  }
}
```

| Field | Required | Description |
|---|---|---|
| `from` | ✅ | Target collection name. Must be in the server's allowed list. |
| `localPath` | ✅ | Field in the current document containing the foreign key. |
| `foreignPath` | ✅ | Field in the target collection to match against (usually `"id"`). |
| `as` | ✅ | Alias under which the joined result is embedded. |

---

## Stage: `resolve`

Fetches a related document from a configured external source (e.g. a CRM, external API).

```json
{
  "resolve": {
    "source": "crm.customer",
    "localPath": "attributes.customerId",
    "as": "crmCustomer"
  }
}
```

| Field | Required | Description |
|---|---|---|
| `source` | ✅ | External source identifier. Must be in the server's allowed list. |
| `localPath` | ✅ | Local field containing the lookup key. |
| `as` | ✅ | Alias for the resolved result. |

---

## Stage: `unwind`

Deconstructs an array field into one document per element.

```json
{
  "unwind": {
    "path": "Appointments",
    "as": "appointment",
    "preserveNull": false,
    "includeIndex": "appointmentIndex"
  }
}
```

| Field | Required | Description |
|---|---|---|
| `path` | ✅ | Dot-notation path to the array field. |
| `as` | ❌ | Alias for the unwound element. |
| `preserveNull` | ❌ | Keep documents where the array is `null` or empty. Default: `false`. |
| `includeIndex` | ❌ | Output field name to store the array element index. |

---

## Stage: `group`

Aggregates documents into groups and computes aggregate values.

```json
{
  "group": {
    "by": [
      { "path": "Status.Name", "as": "status" }
    ],
    "fields": {
      "total":   { "count": true },
      "revenue": { "sum": { "path": "attributes.amount" } },
      "average": { "avg": { "path": "attributes.amount" } }
    }
  }
}
```

### `by` — group-by fields

Each entry is either a **path** or a **dateTrunc**:

```json
{ "path": "Status.Name", "as": "status" }
```

```json
{
  "dateTrunc": { "path": "CreatedAt", "unit": "month" },
  "as": "createdMonth"
}
```

Date truncation units: `year` `quarter` `month` `week` `day` `hour` `minute` `second`

### `fields` — aggregation expressions

| Function | Description | Argument |
|---|---|---|
| `count` | Count of documents in group | `true` |
| `countDistinct` | Count of distinct values | field path |
| `sum` | Sum of a numeric field | field path |
| `avg` | Average of a numeric field | field path |
| `min` | Minimum value | field path |
| `max` | Maximum value | field path |
| `first` | First value in group | field path |
| `last` | Last value in group | field path |
| `push` | Array of all values in group | field path |

#### Argument types

```json
{ "sum": { "path": "attributes.amount" } }
{ "sum": { "literal": 1 } }
{ "sum": { "$var": "multiplier" } }
{ "sum": { "multiply": [ { "path": "qty" }, { "path": "price" } ] } }
```

Arithmetic operators in expressions: `add` `subtract` `multiply` `divide` `coalesce`

---

## Stage: `project`

Selects which fields to include or exclude in the output.

```json
{
  "project": {
    "id": 1,
    "MatchCode": 1,
    "RegistrationPlate": { "RegistrationIdentifier": 1 },
    "Status": { "Name": 1 },
    "Appointments": { "NextDate": 1 }
  }
}
```

| Value | Meaning |
|---|---|
| `1` | Include this field |
| `0` | Exclude this field |

Both flat dot-notation and nested object syntax are accepted and are equivalent:

```json
{ "RegistrationPlate.RegistrationIdentifier": 1 }
{ "RegistrationPlate": { "RegistrationIdentifier": 1 } }
```

For arrays, projecting a sub-field returns that sub-field from every element:

```json
{ "Appointments": { "NextDate": 1, "Location": 1 } }
```

---

## Stage: `sort`

Orders the result set. Array of one-property objects — field name to direction.

```json
{
  "sort": [
    { "MatchCode": "asc" },
    { "CreatedAt": "desc" }
  ]
}
```

| Direction | Meaning |
|---|---|
| `"asc"` | Ascending (A→Z, 0→9, oldest→newest) |
| `"desc"` | Descending (Z→A, 9→0, newest→oldest) |

A deterministic tie-breaker sort on `id` is automatically appended by the server if `id` is not already present in the sort.

---

## Stage: `page`

Controls result size and cursor-based forward pagination.

```json
{
  "page": {
    "limit": 50,
    "cursor": null,
    "includeTotalCount": false
  }
}
```

| Field | Required | Default | Description |
|---|---|---|---|
| `limit` | ❌ | `50` | Maximum documents to return. Server enforces a maximum. |
| `cursor` | ❌ | `null` | Opaque pagination token from a previous response's `nextCursor`. |
| `includeTotalCount` | ❌ | `false` | When `true`, the response includes the total matching document count. |

### Pagination response shape

```json
{
  "items": [ ...documents ],
  "pageInfo": {
    "hasNextPage": true,
    "nextCursor": "eyJNYXRjaENvZGUiOiJBQkMifQ",
    "totalCount": null
  }
}
```

To fetch the next page, pass `nextCursor` as `cursor` in the next request. When `hasNextPage` is `false`, you have reached the last page.

---

## Full example

```json
{
  "entityType": "vehicle",
  "variables": {
    "orgId": "7feec12f-870f-4087-a676-27e411d570a8"
  },
  "pipeline": [
    {
      "match": {
        "and": [
          { "OrganizationId": { "eq": { "$var": "orgId" } } },
          { "Status.Name":    { "eq": "active" } },
          { "CreatedAt":      { "gte": { "$date": "2024-01-01T00:00:00Z" } } }
        ]
      }
    },
    {
      "lookup": {
        "from": "customers",
        "localPath": "CustomerId",
        "foreignPath": "id",
        "as": "customer"
      }
    },
    {
      "project": {
        "id": 1,
        "MatchCode": 1,
        "RegistrationPlate": { "RegistrationIdentifier": 1 },
        "Status": { "Name": 1 },
        "Appointments": { "NextDate": 1 },
        "customer": { "Name": 1 }
      }
    },
    {
      "sort": [{ "MatchCode": "asc" }]
    },
    {
      "page": { "limit": 50, "cursor": null }
    }
  ]
}
```

---

## Error response

Validation errors return HTTP 400 with the following shape:

```json
{
  "type": "validation_error",
  "title": "Query validation failed.",
  "status": 400,
  "errors": [
    { "code": "UNKNOWN_OPERATOR", "message": "Unknown operator 'eval'.", "path": "amount" }
  ]
}
```

### Validation error codes

| Code | Cause |
|---|---|
| `INVALID_ENTITY_TYPE` | `entityType` is missing or empty |
| `MAX_PIPELINE_STAGES_EXCEEDED` | Too many stages in the pipeline |
| `MAX_LOOKUP_STAGES_EXCEEDED` | Too many `lookup` stages |
| `MAX_UNWIND_STAGES_EXCEEDED` | Too many `unwind` stages |
| `INVALID_FILTER_PATH` | Filter condition has no field path |
| `MISSING_OPERATOR` | Filter condition has no operator |
| `UNKNOWN_OPERATOR` | Operator not in the allowed list |
| `REGEX_TOO_LONG` | Regex pattern exceeds maximum length |
| `INVALID_LOOKUP_SOURCE` | `lookup.from` is missing |
| `DISALLOWED_LOOKUP_SOURCE` | `lookup.from` not in the server allowlist |
| `INVALID_LOOKUP_ALIAS` | `lookup.as` is missing |
| `INVALID_RESOLVE_SOURCE` | `resolve.source` is missing or disallowed |
| `INVALID_RESOLVE_ALIAS` | `resolve.as` is missing |
| `MAX_GROUP_FIELDS_EXCEEDED` | Too many fields in `group.fields` |
| `INVALID_DATE_TRUNC_UNIT` | Unknown `dateTrunc` unit |
| `UNKNOWN_AGG_FUNCTION` | Aggregation function not in the allowed list |
| `MAX_PROJECTION_FIELDS_EXCEEDED` | Too many fields in `project` |
| `INVALID_SORT_DIRECTION` | Sort direction not `"asc"` or `"desc"` |
| `INVALID_PAGE_LIMIT` | `page.limit` is zero or negative |
| `PAGE_SIZE_EXCEEDED` | `page.limit` exceeds the server maximum |
| `INVALID_PATH_DOLLAR` | A field path contains `$` |
| `INVALID_PATH_TRAVERSAL` | A field path contains `..` |
| `EMPTY_PATH_SEGMENT` | A field path has an empty segment |
