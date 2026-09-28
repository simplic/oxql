using OxQL.AspNetCore;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Mongo;
using OxQL.Sample.Models;
using OxQL.Studio;

var builder = WebApplication.CreateBuilder(args);

// ── OxQL Core: every option of the OxQL section (limits, execution, cursor signing, …) ──
builder.Services.AddOxQLCore(builder.Configuration.GetSection("OxQL"));

// ── OxQL MongoDB engine ────────────────────────────────────────────────
// Change the connection string in appsettings.json (or via environment variable
// ConnectionStrings__MongoDB) before running against a real database. The entity model is
// built from the scanned assemblies on the first request, after every registration.
builder.Services.AddOxQLMongo(options =>
{
    options.ConnectionString = builder.Configuration.GetConnectionString("MongoDB");
    options.DatabaseName = builder.Configuration["OxQL:DatabaseName"];
    options.IncludeErrorDetails = builder.Environment.IsDevelopment();
    options.ScanAssemblies(typeof(VehicleBase).Assembly);
});

// ── OxQL ASP.NET Core controller ────────────────────────────────────────
// Routes: POST /OxQL/query, POST /OxQL/batch, GET /OxQL/health, POST /OxQL/explain (the last
// one answers only while OxQL:Explain:Enabled is true in the configuration section above).
builder.Services.AddOxQLAspNetCore();

// ── The organisation scope (mandatory) ──────────────────────────────────
// The engine applies `organizationId eq <this>` at every entry into an entity and refuses to
// start without a provider. Here the organisation comes from a header for demonstration; a
// real host resolves it from the authenticated user's claims.
builder.Services.AddOxQLScope(httpContext =>
    httpContext is not null && Guid.TryParse(httpContext.Request.Headers["OrganizationId"].FirstOrDefault(), out var organisation)
        ? organisation
        : Guid.Parse("7feec12f-870f-4087-a676-27e411d570a8"));

// ── OxQL Studio (dark-mode Monaco query builder at /oxql) ───────────────
builder.Services.AddOxQLStudio(options =>
{
    options.RoutePath = "/oxql";
    options.ApiBasePath = "/OxQL";   // matches the OxQLController route
    options.Title = "OxQL Studio";
    options.EnableExplain = builder.Configuration.GetValue("OxQL:Explain:Enabled", true);
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
app.MapOxQLStudio();

// ── Example minimal-API endpoint: a page of one entity, newest first ───
app.MapGet("/api/{entityType}", async (
    string entityType,
    int limit,
    string? cursor,
    IOxQLQueryService queries,
    CancellationToken ct) =>
{
    limit = Math.Clamp(limit == 0 ? 50 : limit, 1, 500);

    var request = new QueryRequest
    {
        EntityType = entityType,
        Pipeline =
        [
            new PipelineStage { Sort = [new SortField { Path = "createdAt", Direction = "desc" }], Keys = ["sort"] },
            new PipelineStage { Page = new PageStage { Limit = limit, Cursor = string.IsNullOrEmpty(cursor) ? null : cursor }, Keys = ["page"] },
        ]
    };

    return await queries.ExecuteAsync(request, ct) switch
    {
        QueryOutcome.Success success => Results.Ok(success.Result),
        QueryOutcome.Refused refused => Results.Json(refused.Refusal, statusCode: refused.Refusal.Status),
        _ => Results.StatusCode(500),
    };
})
.WithName("ListEntities")
.WithSummary("List documents of a given entity type with cursor paging")
.WithTags("Documents");

app.Run();

/// <summary>Exposes the entry point to the host tests (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
