# OxQL (oxsql)

A flexible document query engine for .NET, providing a GraphQL-like query API backed by MongoDB. Accepts JSON-based query requests for dynamic document structures that are not fully known at compile time.

## Overview

OxQL provides:
- **JSON-based query language** with filtering, sorting, paging, projection, lookups, unwind, and grouping
- **GraphQL-style operators** (eq, gte, in, contains, etc.) translated to MongoDB operators
- **Cursor-based pagination** with opaque base64url cursors
- **Query plan caching** for optimized repeated query execution
- **Security-first design** with validation, path sanitization, and configurable limits
- **Provider-neutral core** with MongoDB adapter (extensible to other backends)

## Solution Structure

```
OxQL.Core/    - Interfaces, models, validation, normalization, caching, cursor logic
OxQL.Mongo/   - MongoDB adapter (pipeline builder, filter translation, cursor paging)
OxQL.Tests/   - xUnit tests with FluentAssertions
```

## Quick Start

```csharp
// Simple setup
var collection = database.GetCollection<BsonDocument>("documents");
var options = new OxQLOptions
{
    MaxPageSize = 500,
    DefaultPageSize = 50,
    AllowedLookupSources = { "customers", "products" }
};

var executor = new MongoQueryExecutor(collection, options);
var response = await executor.ExecuteAsync(request, cancellationToken);
```

### Dependency Injection

```csharp
services.AddOxQLCore(options =>
{
    options.MaxPageSize = 500;
    options.DefaultPageSize = 50;
    options.AllowedLookupSources = new() { "customers", "products" };
    options.AllowedResolveSources = new() { "crm.customer" };
    options.QueryPlanCacheTtl = TimeSpan.FromMinutes(30);
});

services.AddOxQLMongo(options =>
{
    options.ConnectionString = "mongodb://localhost:27017";
    options.DatabaseName = "myapp";
    options.CollectionName = "documents";
});
```

## Example Query

```json
{
  "entityType": "invoice",
  "variables": {
    "minAmount": 1000,
    "statuses": ["approved", "paid"]
  },
  "pipeline": [
    {
      "match": {
        "and": [
          { "attributes.amount": { "gte": { "$var": "minAmount" } } },
          { "attributes.status": { "in": { "$var": "statuses" } } }
        ]
      }
    },
    {
      "lookup": {
        "from": "customers",
        "localPath": "attributes.customerId",
        "foreignPath": "id",
        "as": "customer"
      }
    },
    {
      "sort": [
        { "createdAt": "desc" }
      ]
    },
    {
      "page": {
        "limit": 50,
        "cursor": null,
        "includeTotalCount": false
      }
    }
  ]
}
```

### Response

```json
{
  "items": [...],
  "pageInfo": {
    "hasNextPage": true,
    "nextCursor": "eyJmaWVsZHMiOlt7InBhdGgiOiJjcmVhdGVkQXQiLC...",
    "totalCount": null
  }
}
```

## Filter Syntax

A filter condition uses the field path as the key and an operator-value map as the value:

```json
{ "FieldPath": { "op": value } }
```

Logical grouping with `and`, `or`, and `not`:

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

`and` / `or` / `not` can be nested arbitrarily.

### Filter operators

| Operator | Description | Example value |
|---|---|---|
| `eq` | Equal ¹ | `"active"` |
| `neq` | Not equal ¹ | `"deleted"` |
| `gt` | Greater than | `100` |
| `gte` | Greater than or equal | `100` |
| `lt` | Less than | `100` |
| `lte` | Less than or equal | `100` |
| `in` | Value in array | `["a", "b"]` |
| `nin` | Value not in array | `["x", "y"]` |
| `contains` | String contains substring ¹ | `"abc"` |
| `startsWith` | String starts with | `"pre"` |
| `endsWith` | String ends with | `"fix"` |
| `exists` | Field exists | `true` / `false` |
| `regex` | Regular expression match | `"^ABC.*"` |

¹ Supports [filter condition options](#filter-condition-options) (`ignoreCase`).

### Filter condition options

The operators `eq`, `neq`, and `contains` accept an optional `"options"` key alongside the operator value to modify comparison behaviour.

| Option | Type | Default | Description |
|---|---|---|---|
| `ignoreCase` | `boolean` | `false` | Makes string comparisons case-insensitive. |

```json
{ "Status.Name": { "eq": "Active", "options": { "ignoreCase": true } } }
{ "Status.Name": { "neq": "deleted", "options": { "ignoreCase": true } } }
{ "attributes.description": { "contains": "urgent", "options": { "ignoreCase": true } } }
```

> **Note:** When `ignoreCase` is `true`, `eq` and `neq` use a case-insensitive regex anchored at both ends (`^value$` with the `i` flag). `contains` uses an unanchored regex with the `i` flag.

### Variables

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

## Supported Pipeline Stages

| Stage | Description |
|-------|-------------|
| `match` | Filter documents |
| `lookup` | Join with another collection |
| `resolve` | Resolve from external source (interface) |
| `unwind` | Deconstruct array field |
| `group` | Group and aggregate |
| `project` | Select output fields |
| `sort` | Order results |
| `page` | Cursor-based pagination |

Recommended stage order: `match → lookup → resolve → unwind → group → project → sort → page`

### Sort syntax

Each sort entry is an object with the field name as the key and direction as the value:

```json
{
  "sort": [
    { "MatchCode": "asc" },
    { "CreatedAt": "desc" }
  ]
}
```

A deterministic tie-breaker sort on `id` is automatically appended by the server if `id` is not already present.

## Aggregation Functions

| Function | Description |
|----------|-------------|
| `count` | Count documents |
| `countDistinct` | Count distinct values |
| `sum` | Sum values |
| `avg` | Average values |
| `min` | Minimum value |
| `max` | Maximum value |
| `first` | First value |
| `last` | Last value |
| `push` | Collect into array |

## Expression Operators

| Operator | Description |
|----------|-------------|
| `path` | Field reference |
| `literal` | Literal value |
| `$var` | Variable reference |
| `add` | Addition |
| `subtract` | Subtraction |
| `multiply` | Multiplication |
| `divide` | Division |
| `coalesce` | First non-null value |
| `dateTrunc` | Truncate date to unit |

## MongoDB Setup

### Document Shape

Documents should follow this structure:

```json
{
  "_id": "unique-id",
  "id": "unique-id",
  "entityType": "invoice",
  "createdAt": "2024-01-15T10:00:00Z",
  "attributes": {
    "amount": 1500.00,
    "status": "approved",
    "customerId": "cust-123"
  }
}
```

### Recommended Indexes

```javascript
// Entity type + common sort
db.documents.createIndex({ entityType: 1, createdAt: -1, _id: 1 });

// Lookup foreign keys
db.customers.createIndex({ _id: 1 });
db.products.createIndex({ _id: 1 });
```

## Performance Notes

- **Query Plan Caching**: Plans are cached by query shape (excluding variable values). Same-shaped queries with different variable values reuse cached plans.
- **Cursor Paging**: Uses efficient range queries instead of skip/offset. Ideal for large datasets.
- **Pipeline Optimization**: Stages are executed in order. Place `match` stages early to reduce document count for subsequent stages.
- **Total Count**: `includeTotalCount: true` requires an additional aggregation pipeline execution. Avoid for performance-critical queries.
- **Limit + 1 Strategy**: Fetches one extra document to determine `hasNextPage` without a count query.

## Security Limits

All limits are configurable via `OxQLOptions`:

| Setting | Default | Description |
|---------|---------|-------------|
| `MaxPageSize` | 500 | Maximum allowed page size |
| `DefaultPageSize` | 50 | Default page size |
| `MaxPipelineStages` | 20 | Maximum pipeline stages |
| `MaxLookupStages` | 5 | Maximum lookup/join stages |
| `MaxUnwindStages` | 5 | Maximum unwind stages |
| `MaxGroupFields` | 20 | Maximum group aggregation fields |
| `MaxProjectionFields` | 500 | Maximum projection fields |
| `RegexMaxLength` | 200 | Maximum regex pattern length |
| `QueryPlanCacheTtl` | 30 min | Cache entry lifetime |
| `QueryPlanCacheMaxEntries` | 1000 | Maximum cached plans |

### Path Security
- Paths containing `$` are rejected
- Paths containing `..` are rejected
- Empty path segments are rejected
- Only allowlisted lookup/resolve sources are permitted
- Raw MongoDB operators are never exposed in the public API

## Extension Points

### IExternalResolver
Implement for external data source resolution:

```csharp
public class CrmCustomerResolver : IExternalResolver
{
    public string Source => "crm.customer";

    public async Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
        IReadOnlyList<string> keys, CancellationToken ct)
    {
        // Fetch from external CRM API
    }
}
```

### IQueryAdapter<T>
Implement for non-MongoDB backends:

```csharp
public class PostgresQueryAdapter : IQueryAdapter<JsonDocument>
{
    public Task<QueryResponse<JsonDocument>> ExecuteAsync(
        QueryPlan plan, QueryVariables? variables, CancellationToken ct)
    {
        // Translate to SQL
    }
}
```

### Custom Validation
Extend `IQueryValidator` for domain-specific rules.

## Error Response

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

## Running Tests

```bash
dotnet test OxQL.Tests
```

## Requirements

- .NET 10+
- MongoDB 5.0+ (for `$dateTrunc` support)
- MongoDB.Driver 3.x
