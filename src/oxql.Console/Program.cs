using System.Text.Json;
using OxQL.Core.Models;
using OxQL.Core.Normalization;
using OxQL.Core.Validation;

// ──────────────────────────────────────────────────────────────────────
// OxQL Sample – demonstrates building, validating, normalizing,
// and serializing a query request without a live MongoDB connection.
// ──────────────────────────────────────────────────────────────────────

var options = new OxQLOptions
{
    MaxPageSize = 500,
    DefaultPageSize = 50,
    AllowedLookupSources = ["customers", "products"],
    AllowedResolveSources = ["crm.customer"]
};

// ── 1. Build a query request using the object model ─────────────────

var request = new QueryRequest
{
    EntityType = "invoice",
    Variables = new QueryVariables
    {
        Values = new Dictionary<string, object?>
        {
            ["minAmount"] = JsonDocument.Parse("1000").RootElement,
            ["statuses"] = JsonDocument.Parse("""["approved", "paid"]""").RootElement
        }
    },
    Pipeline =
    [
        // Filter: amount >= $minAmount AND status IN $statuses
        new PipelineStage
        {
            Match = new MatchStage
            {
                And =
                [
                    new FilterCondition
                    {
                        Path = "attributes.amount",
                        Op = "gte",
                        Value = JsonDocument.Parse("""{ "$var": "minAmount" }""").RootElement
                    },
                    new FilterCondition
                    {
                        Path = "attributes.status",
                        Op = "in",
                        Value = JsonDocument.Parse("""{ "$var": "statuses" }""").RootElement
                    }
                ]
            }
        },
        // Join customers collection
        new PipelineStage
        {
            Lookup = new LookupStage
            {
                From = "customers",
                LocalPath = "attributes.customerId",
                ForeignPath = "id",
                As = "customer"
            }
        },
        // Select specific fields
        new PipelineStage
        {
            Project = new ProjectStage
            {
                Fields = new Dictionary<string, int>
                {
                    ["id"] = 1,
                    ["entityType"] = 1,
                    ["attributes.amount"] = 1,
                    ["attributes.status"] = 1,
                    ["customer.attributes.name"] = 1
                }
            }
        },
        // Sort by amount descending
        new PipelineStage
        {
            Sort =
            [
                new SortField { Path = "attributes.amount", Direction = "desc" }
            ]
        },
        // Page: first 25 results
        new PipelineStage
        {
            Page = new PageStage { Limit = 25, IncludeTotalCount = true }
        }
    ]
};

// ── 2. Serialize to JSON (what a client would send) ─────────────────

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = true,
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
};

var json = JsonSerializer.Serialize(request, jsonOptions);
Console.WriteLine("═══ Query Request (JSON) ═══");
Console.WriteLine(json);
Console.WriteLine();

// ── 3. Deserialize back (simulating server receiving the request) ────

var deserialized = JsonSerializer.Deserialize<QueryRequest>(json, jsonOptions)!;
Console.WriteLine($"✓ Deserialized: entityType = \"{deserialized.EntityType}\", " +
                  $"pipeline stages = {deserialized.Pipeline.Count}");
Console.WriteLine();

// ── 4. Validate the request ─────────────────────────────────────────

var validator = new QueryValidator(options);
var validation = validator.Validate(deserialized);

if (validation.IsValid)
{
    Console.WriteLine("✓ Validation passed");
}
else
{
    Console.WriteLine("✗ Validation failed:");
    foreach (var error in validation.Errors)
    {
        Console.WriteLine($"  [{error.Code}] {error.Message}");
    }
}
Console.WriteLine();

// ── 5. Normalize the request ────────────────────────────────────────

var normalizer = new QueryRequestNormalizer(options);
var normalized = normalizer.Normalize(deserialized);

var sortStage = normalized.Pipeline.FirstOrDefault(s => s.Sort is not null);
if (sortStage?.Sort is not null)
{
    Console.WriteLine("✓ Normalized sort (tie-breaker added):");
    foreach (var field in sortStage.Sort)
    {
        Console.WriteLine($"    {field.Path} {field.Direction}");
    }
}
Console.WriteLine();

// ── 6. Generate cache key ───────────────────────────────────────────

var cacheKey = normalizer.GenerateCacheKey(normalized);
Console.WriteLine($"✓ Cache key: {cacheKey}");
Console.WriteLine();

// ── 7. Show what a disallowed query looks like ──────────────────────

Console.WriteLine("═══ Validation Rejection Examples ═══");
Console.WriteLine();

var badRequest = new QueryRequest
{
    EntityType = "invoice",
    Pipeline =
    [
        new PipelineStage
        {
            Match = new MatchStage
            {
                Condition = new FilterCondition
                {
                    Path = "$where",  // Dangerous!
                    Op = "eval"       // Unknown operator
                }
            }
        },
        new PipelineStage
        {
            Lookup = new LookupStage
            {
                From = "secrets",  // Not in allowlist
                LocalPath = "attributes.secretId",
                ForeignPath = "id",
                As = "secret"
            }
        },
        new PipelineStage
        {
            Page = new PageStage { Limit = 9999 }  // Exceeds max
        }
    ]
};

var badValidation = validator.Validate(badRequest);
Console.WriteLine($"  Request valid? {badValidation.IsValid}");
Console.WriteLine($"  Errors ({badValidation.Errors.Count}):");
foreach (var error in badValidation.Errors)
{
    Console.WriteLine($"    [{error.Code}] {error.Message}");
}
Console.WriteLine();

// ── 8. Demonstrate JSON-based query (as a client would submit) ──────

Console.WriteLine("═══ Parsing a Raw JSON Query ═══");
Console.WriteLine();

var rawJson = """
{
  "entityType": "order",
  "variables": {
    "customerId": "cust-42"
  },
  "pipeline": [
    {
      "match": {
        "and": [
          { "attributes.customerId": { "eq": { "$var": "customerId" } } },
          { "attributes.total": { "gt": 0 } }
        ]
      }
    },
    {
      "sort": [
        { "createdAt": "desc" }
      ]
    },
    {
      "page": { "limit": 10 }
    }
  ]
}
""";

var parsed = JsonSerializer.Deserialize<QueryRequest>(rawJson, jsonOptions)!;
var parsedValidation = validator.Validate(parsed);
Console.WriteLine($"  Parsed entityType: \"{parsed.EntityType}\"");
Console.WriteLine($"  Pipeline stages: {parsed.Pipeline.Count}");
Console.WriteLine($"  Valid: {parsedValidation.IsValid}");
Console.WriteLine();

// ── 9. Note about execution ─────────────────────────────────────────

Console.WriteLine("═══ Execution Note ═══");
Console.WriteLine();
Console.WriteLine("  To execute against MongoDB, create a MongoQueryExecutor:");
Console.WriteLine();
Console.WriteLine("    var collection = database.GetCollection<BsonDocument>(\"documents\");");
Console.WriteLine("    var executor = new MongoQueryExecutor(collection, options);");
Console.WriteLine("    var response = await executor.ExecuteAsync(request, cancellationToken);");
Console.WriteLine();
Console.WriteLine("  The response contains:");
Console.WriteLine("    response.Items        – the result documents");
Console.WriteLine("    response.PageInfo     – { HasNextPage, NextCursor, TotalCount }");
Console.WriteLine();
