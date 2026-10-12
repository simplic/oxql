// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Bson;
using MongoDB.Driver;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Core.Legacy;
using OxQL.Core.Models;
using OxQL.Mongo.Compat;

namespace OxQL.Mongo.Legacy;

/// <summary>
/// The pipeline ported from the 1.1 query path, over this engine's collections: the request as the
/// caller wrote it becomes one aggregate on the entity's collection (and one more for a count), and
/// the documents come back as stored, rendered by the v1 converter, so existing callers get the
/// rows, the envelope and the refusal codes they got. The caller's organisation is matched before
/// any stage, every database call carries a time limit and the caller's cancellation, and a fault
/// says where its detail is.
/// </summary>
internal sealed class LegacyPipeline : ILegacyPipeline
{
    private readonly IEntityModelProvider models;
    private readonly IAggregateRunner runner;
    private readonly OxQLOptions options;
    private readonly ILogger logger;

    /// <summary>The pipeline over <paramref name="models"/>' collections, run by <paramref name="runner"/>.</summary>
    public LegacyPipeline(IEntityModelProvider models, IAggregateRunner runner, OxQLOptions options, ILogger<LegacyPipeline>? logger = null)
    {
        this.models = models ?? throw new ArgumentNullException(nameof(models));
        this.runner = runner ?? throw new ArgumentNullException(nameof(runner));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.logger = logger ?? NullLogger<LegacyPipeline>.Instance;
    }

    /// <inheritdoc/>
    public async Task<QueryOutcome> ExecuteAsync(JsonElement request, RequestContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        // No organisation, no rows: the same answer the contract 2 engine gives.
        if (context.Organisation is not { } organisation)
            return QueryOutcome.Of(Refusal.AccessDenied("The request carries no organisation."));

        // The one filter every read rests on: a host that names no member for it, or something that is no member, reads nothing.
        if (string.IsNullOrWhiteSpace(options.Compat.Legacy.OrganisationPath) || options.Compat.Legacy.OrganisationPath.Contains('$') || options.Compat.Legacy.OrganisationPath.Split('.').Any(string.IsNullOrWhiteSpace))
            return QueryOutcome.Of(Refusal.AccessDenied("The host names no member that holds a document's organisation ('Compat:Legacy:OrganisationPath'); a contract 1 request reads nothing without one."));

        LegacyRequest read;

        try
        {
            read = LegacyRequestReader.Read(request);
        }
        catch (Exception exception) when (exception is LegacyBodyException or InvalidOperationException or FormatException)
        {
            return Refused([LegacyValidator.Error("INVALID_REQUEST", exception is LegacyBodyException ? exception.Message : "The request body is not a contract 1 query.")]);
        }

        // The 1.1 registry held the entity ids without regard to case; the model holds them, and the ids an entity retired, lower-cased.
        if (string.IsNullOrWhiteSpace(read.EntityType) || !models.Model.TryResolve(read.EntityType.Trim().ToLowerInvariant(), out var entity, out _))
            return Refused([LegacyValidator.Error("UNKNOWN_TYPE", $"No OxQL type registration found for entity type '{read.EntityType}'.")]);

        if (read.Faulty)
            return Fault("The request holds null where a stage or a sort entry stands.");

        var legacy = options.Compat.Legacy;
        var errors = new LegacyValidator(options.Limits, legacy).Validate(read);

        if (errors.Count > 0)
            return Refused(errors);

        LegacyAggregate aggregate;

        try
        {
            aggregate = LegacyAggregate.Build(
                read,
                LegacyAggregate.Equality(legacy.OrganisationPath, new BsonString(organisation.ToString()), negate: false, ignoreCase: false),
                options.Limits.DefaultPageSize,
                options.Limits.MaxPageSize,
                options.Limits.RegexMaxLength);
        }
        catch (LegacyRefusalException refusal)
        {
            return Refused([refusal.Error]);
        }
        catch (Exception exception) when (exception is InvalidCastException or InvalidOperationException or NullReferenceException or ArgumentNullException)
        {
            // An operand of a kind its operator cannot take ('in' with a text, 'contains' with a number), a group key
            // without a name: no aggregate can be built, and the answer is the 500 contract 1 callers got. The message
            // says that the request is at fault.
            logger.LogInformation(exception, "OxQL legacy pipeline could not build the aggregate of a contract 1 request for {Entity} correlation={CorrelationId}", entity.Id, context.CorrelationId);

            return Fault("The request cannot be built into a query: an operand is of a kind its operator does not take, or a stage lacks a member it needs.");
        }

        // The time ceiling is the legacy pipeline's own and generous, so a report that runs long still answers; it is
        // not the contract 2 engine's. What a batch leaves its entry bounds it further.
        var ceiling = legacy.EffectiveMaxTimeMs;

        if (context.MaxTimeMs is { } left and > 0)
            ceiling = Math.Min(ceiling, left);

        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // One ceiling for the request: the count runs under what the page left of it.
        AggregateRunOptions Run() => new((int)Math.Max(1, ceiling - (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds), AllowDiskUse: null) { Comment = context.CorrelationId };

        try
        {
            var rows = (await runner.AggregateAsync(entity, aggregate.Page, Run(), cancellationToken).ConfigureAwait(false)).ToList();
            var more = rows.Count > aggregate.Limit;

            if (more)
                rows.RemoveRange(aggregate.Limit, rows.Count - aggregate.Limit);

            long? total = null;

            // The count follows the page, as it did: one aggregate after the other.
            if (aggregate.Count is { } count)
                total = (await runner.AggregateAsync(entity, count, Run(), cancellationToken).ConfigureAwait(false)).FirstOrDefault() is { } counted && counted.Contains("totalCount") ? counted["totalCount"].ToInt64() : 0;

            return QueryOutcome.Of(new QueryResult
            {
                Items = rows.Select(CompatRows.Encode).ToList<JsonNode?>(),
                PageInfo = new PageInfo
                {
                    HasNextPage = more,
                    NextCursor = more && rows.Count > 0 ? LegacyCursor.Write(aggregate.Sort, rows[^1]) : null,
                    TotalCount = total,
                },
            });
        }
        catch (Exception exception) when (exception is MongoExecutionTimeoutException or TimeoutException)
        {
            return QueryOutcome.Of(Refusal.Timeout($"The query did not finish within {ceiling} ms."));
        }
        catch (MongoCommandException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // What the database refuses of a request written against no model: a projection of nothing, a name it cannot take.
            logger.LogInformation(exception, "OxQL legacy pipeline: the database refused the aggregate of a contract 1 request for {Entity} correlation={CorrelationId}", entity.Id, context.CorrelationId);

            return Fault($"The database refused the query ({exception.CodeName}); the detail is in the service log under the correlation id '{context.CorrelationId}'.");
        }
    }

    /// <summary>The contract 1 answer to a fault: 500 under its type and title, the status repeated in the body, one error <c>INTERNAL_ERROR</c>.</summary>
    private static QueryOutcome Fault(string message) => QueryOutcome.Of(new Refusal
    {
        Type = "internal_error",
        Title = "An unexpected error occurred while executing the query.",
        Status = 500,
        StatusInBody = 500,
        Errors = [LegacyValidator.Error("INTERNAL_ERROR", message)],
    });

    /// <summary>The 1.1 engine's refusal of a request: 400 under its title, the status repeated in the body, each error with its code, message and path.</summary>
    private static QueryOutcome Refused(IReadOnlyList<QueryValidationError> errors) => QueryOutcome.Of(new Refusal
    {
        Type = "validation_error",
        Title = "Query validation failed.",
        Status = 400,
        StatusInBody = 400,
        Errors = errors,
    });
}
