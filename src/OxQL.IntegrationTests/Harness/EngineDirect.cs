using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.Model;
using OxQL.Mongo;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// The engine without a host, for a case that needs an entity model the lab model does not have
/// (a hand-declared pair of entities, say) or needs to look at the compiled stages: bind,
/// compile, and run the stages on a real server, or run the whole engine over a database. Pair
/// it with <see cref="MongoFixture.CreateDatabaseAsync"/>, so the case owns a database that is
/// dropped afterwards.
/// </summary>
public sealed class EngineDirect(EntityModel model)
{
    /// <summary>The organisation every request is scoped to unless a context says otherwise.</summary>
    public static readonly Guid Organisation = LabIdentity.OrganisationA;

    /// <summary>The same stored form the organisation id has in every lab row.</summary>
    public static BsonBinaryData OrganisationValue => new(Organisation, GuidRepresentation.Standard);

    private static readonly JsonSerializerOptions Wire = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public EntityModel Model { get; } = model;

    /// <summary>The cursor codec of this engine; its key is random per instance.</summary>
    public CursorCodec Cursors => new(signingKey);

    /// <summary>Options with this engine's signing key, changed by <paramref name="configure"/>.</summary>
    public OxQLOptions Options(Action<OxQLOptions>? configure = null)
    {
        var options = new OxQLOptions { Cursor = { SigningKey = signingKey } };

        configure?.Invoke(options);

        return options;
    }

    /// <summary>A request context: organisation A, contract 2, no addons, unless told otherwise.</summary>
    public RequestContext Context(OxQLOptions? options = null, Guid? organisation = null, int contract = 2) => new()
    {
        Organisation = organisation ?? Organisation,
        Options = options ?? Options(),
        Contract = contract,
    };

    /// <summary>A request over <paramref name="entity"/> with a pipeline written as a JSON array.</summary>
    public static QueryRequest Request(string entity, string pipeline, string? variables = null) =>
        JsonSerializer.Deserialize<QueryRequest>(Json.Request(entity, pipeline, variables).ToJsonString(), Wire)
        ?? throw new InvalidOperationException("The request did not parse.");

    /// <summary>Binds, and asserts it bound.</summary>
    public async Task<BoundPipeline> BindAsync(string entity, string pipeline, RequestContext? context = null)
    {
        var outcome = await new Binder(Model, Cursors).BindAsync(Request(entity, pipeline), context ?? Context(), CancellationToken.None);

        outcome.Should().BeOfType<BindOutcome.Bound>(outcome is BindOutcome.Failed failed ? Describe(failed.Refusal) : "");

        return ((BindOutcome.Bound)outcome).Pipeline;
    }

    /// <summary>Binds and compiles to the stages the engine would run.</summary>
    public async Task<CompiledQuery> CompileAsync(string entity, string pipeline, CompileOptions? options = null) =>
        MongoCompiler.Compile(await BindAsync(entity, pipeline), options ?? new CompileOptions(10_000, null, 100_000));

    /// <summary>Runs compiled page (or count) stages over a collection, under the collation the compiler chose.</summary>
    public static async Task<List<BsonDocument>> RunAsync(IMongoDatabase database, string collection, CompiledQuery compiled, bool count = false)
    {
        var options = new AggregateOptions();

        if (compiled.Collation is { } collation)
            options.Collation = Collation.FromBsonDocument(collation);

        var stages = count ? compiled.CountStages ?? throw new InvalidOperationException("The query compiled no count stages.") : compiled.PageStages;

        return await (await database.GetCollection<BsonDocument>(collection).AggregateAsync(PipelineDefinition<BsonDocument, BsonDocument>.Create(stages), options)).ToListAsync();
    }

    /// <summary>The whole engine over one database, as a host builds it (no remote client).</summary>
    public MongoQueryEngine Engine(IMongoClient client, string database, OxQLOptions? options = null) =>
        new(new StaticEntityModelProvider(Model), new MongoAggregateRunner(client, database), Cursors, options ?? Options(), includeErrorDetails: true);

    /// <summary>Executes through the whole engine and asserts a success; the rows as the wire writes them.</summary>
    public async Task<JsonArray> ExecuteAsync(IQueryEngine engine, string entity, string pipeline, RequestContext? context = null)
    {
        var outcome = await engine.ExecuteAsync(Request(entity, pipeline), context ?? Context());

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? Describe(refused.Refusal) : "");

        return new JsonArray(((QueryOutcome.Success)outcome).Result.Items.Select(item => item?.DeepClone()).ToArray());
    }

    public static string Describe(Refusal refusal) =>
        $"{refusal.Status} {refusal.Type}: " + string.Join("; ", (refusal.Errors ?? []).Select(error => $"{error.Code}@{error.Stage} {error.Path}: {error.Message}"));
}
