using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.Core;
using OxQL.Core.Filtering;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Sample.Models;
using OxQL.Studio;

var builder = WebApplication.CreateBuilder(args);

// ── OxQL Core ──────────────────────────────────────────────────────────
// Reads AllowedLookupSources etc. from configuration section "OxQL".
var oxqlSection = builder.Configuration.GetSection("OxQL");

builder.Services.AddOxQLCore(options =>
{
    options.MaxPageSize = oxqlSection.GetValue("MaxPageSize", 500);
    options.DefaultPageSize = oxqlSection.GetValue("DefaultPageSize", 50);

    var allowedSources = oxqlSection.GetSection("AllowedLookupSources").Get<string[]>() ?? [];
    foreach (var source in allowedSources)
        options.AllowedLookupSources.Add(source);
});

// ── OxQL MongoDB adapter ────────────────────────────────────────────────
// Change the connection string in appsettings.json (or via environment variable
// ConnectionStrings__MongoDB) before running against a real database.
builder.Services.AddOxQLMongo(options =>
{
    options.ConnectionString = "mongodb+srv://local-usr-staging:1UeLDOOnCyJNYcgj@simplic-oxs-mongodb-sta.2iwcv.mongodb.net/admin?retryWrites=false&loadBalanced=false&replicaSet=atlas-l7f4bg-shard-0&readPreference=primary&connectTimeoutMS=10000&authSource=admin&authMechanism=SCRAM-SHA-1&3t.uriVersion=3&3t.connection.name=atlas-staging&3t.databases=admin,simplic_oxs_staging&3t.alwaysShowAuthDB=true&3t.alwaysShowDBFromUserRole=true&3t.sslTlsVersion=TLS";
    options.DatabaseName = "simplic_oxs_staging_vehicle";
    options.ScanAssemblies(typeof(VehicleBase).Assembly);
});

// ── OxQL ASP.NET Core controller ────────────────────────────────────────
// BsonDocument is the MongoDB document type; swap for your own type if needed.
builder.Services.AddOxQLAspNetCore<BsonDocument>(options =>
{
    options.RoutePrefix = "api/oxql";
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();

    // ── Optional endpoint protection ────────────────────────────────────
    // Set RequireAuthorization = true to force authentication on the query/types
    // endpoints (the /health probe always stays anonymous). Optionally set a named
    // policy via AuthorizationPolicy. Authentication/authorization middleware must be
    // configured separately (AddAuthentication/AddAuthorization + UseAuthentication/UseAuthorization).
    // options.RequireAuthorization = true;
    // options.AuthorizationPolicy = "OxQLReader";
});

// ── Multi-tenant query injection (example) ──────────────────────────────
// Forces an OrganizationId filter onto every OxQL query using a root-level AND.
// The value is passed as a query variable ($var) so cached query plans stay
// tenant-safe. Here we read the tenant from a request header for demonstration;
// a real app would resolve it from the authenticated user's claims, e.g.
//   ctx.User?.FindFirst("organizationId")?.Value
builder.Services.AddOxQLQueryFilter(_ =>
    [InjectedFilter.Create("OrganizationId", "7feec12f-870f-4087-a676-27e411d570a8")]);



// ── OxQL Studio (dark-mode Monaco query builder at /oxql) ───────────────
builder.Services.AddOxQLStudio(options =>
{
    options.RoutePath = "/oxql";
    options.ApiBasePath = "/OxQL";   // matches the OxQLController route
    options.Title = "OxQL Studio";
});

// ── Standard ASP.NET Core services ─────────────────────────────────────
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(opts =>
{
    opts.SwaggerDoc("v1", new()
    {
        Title = "OxQL Sample API",
        Version = "v1",
        Description = "Sample application demonstrating the OxQL flexible document query engine."
    });
});

var app = builder.Build();

app.UsePathBase("/vehicle-api/v2");

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers();

// ── OxQL Studio UI ──────────────────────────────────────────────────────
// Dark-mode Monaco query builder available at /oxql
app.MapOxQLStudio();

// ── Example minimal-API endpoints ──────────────────────────────────────
// These show what a real application might expose alongside OxQL.

// Convenience GET that builds an OxQL query for a single entity type
app.MapGet("/api/{entityType}", async (
    string entityType,
    int limit,
    string? cursor,
    IQueryExecutor<BsonDocument> executor,
    CancellationToken ct) =>
{
    limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 500);

    var request = new QueryRequest
    {
        EntityType = entityType,
        Pipeline =
        [
            new PipelineStage
            {
                Sort = [new SortField { Path = "createdAt", Direction = "desc" }]
            },
            new PipelineStage
            {
                Page = new PageStage { Limit = limit, Cursor = string.IsNullOrEmpty(cursor) ? null : cursor }
            }
        ]
    };

    try
    {
        var response = await executor.ExecuteAsync(request, ct);
        return Results.Ok(response);
    }
    catch (QueryValidationException ex)
    {
        return Results.BadRequest(new { errors = ex.Errors });
    }
})
.WithName("ListEntities")
.WithSummary("List documents of a given entity type with cursor paging")
.WithTags("Documents");

app.Run();
