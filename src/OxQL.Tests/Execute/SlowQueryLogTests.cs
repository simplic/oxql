using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The slow-query line: written at warning level when a request's whole time is over
/// <c>Execution:SlowQueryMs</c>, with the entity, the stage kinds and the flags that shaped it,
/// and never an operand; silent under the threshold and when the threshold is off.
/// </summary>
public class SlowQueryLogTests
{
    private const string Order = "probe.order";
    private const string Operand = "needle-7f3a";

    private static readonly string Regex = $$"""[{ "match": { "number": { "contains": "{{Operand}}" } } }, { "sort": [{ "number": "asc" }] }, { "page": { "limit": 5, "includeTotalCount": true } }]""";
    private const string Plain = """[{ "match": { "flag": { "eq": true } } }, { "page": { "limit": 5 } }]""";
    private const string SortedBeforeAGroup = """[{ "sort": [{ "number": "asc" }] }, { "group": { "by": [{ "path": "state", "as": "st" }], "fields": { "n": { "count": true } } } }, { "page": { "limit": 5 } }]""";
    private const string Remote = """[{ "resolve": { "path": "contactNumber", "as": "contact" } }, { "page": { "limit": 5 } }]""";

    /// <summary>Answers after a delay, so the request's time is what the test says it is.</summary>
    private sealed class SlowRunner(TimeSpan delay) : IAggregateRunner
    {
        public async Task<IReadOnlyList<BsonDocument>> AggregateAsync(EntityDef entity, IReadOnlyList<BsonDocument> stages, AggregateRunOptions options, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);

            return stages.Count > 0 && stages[^1].Contains("$count")
                ? [new BsonDocument("n", 1)]
                : [new BsonDocument { ["_id"] = new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard), ["Number"] = "N-1", ["OrganizationId"] = new BsonBinaryData(BindHost.Organisation, GuidRepresentation.Standard) }];
        }
    }

    private sealed class Recorder : ILogger<MongoQueryEngine>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
                Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private static async Task<List<string>> SlowLines(int thresholdMs, string pipeline, TimeSpan delay, IRemoteQueryClient? remote = null)
    {
        var recorder = new Recorder();
        var options = BindHost.Options(options => options.Execution.SlowQueryMs = thresholdMs);
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new SlowRunner(delay), BindHost.Cursors, options, remote, recorder);
        var outcome = await engine.ExecuteAsync(BindHost.Request(Order, pipeline), BindHost.Context(options) with { CorrelationId = "corr-1" });

        outcome.Should().BeOfType<QueryOutcome.Success>(outcome is QueryOutcome.Refused refused ? BindHost.Describe(refused.Refusal) : "");

        return recorder.Entries.Where(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("slow query", StringComparison.Ordinal)).Select(entry => entry.Message).ToList();
    }

    [Fact]
    public async Task A_request_over_the_threshold_is_logged_once_with_what_shaped_it_and_never_its_operands()
    {
        var lines = await SlowLines(1, Regex, TimeSpan.FromMilliseconds(40));

        var line = lines.Should().ContainSingle().Subject;

        line.Should().Contain("OxQL slow query probe.order");
        line.Should().Contain("stages=match,sort,page");
        line.Should().Contain("thresholdMs=1");
        line.Should().Contain("rows=1");
        line.Should().Contain("regex=True");
        line.Should().Contain("unboundedSort=False", "the sort is followed by the page's limit");
        line.Should().Contain("count=True");
        line.Should().Contain("remote=False");
        line.Should().Contain("outcome=ok");
        line.Should().Contain("correlation=corr-1");
        line.Should().NotContain(Operand, "an operand is the caller's data and does not belong in a log");
    }

    [Fact]
    public async Task The_flags_say_what_was_involved()
    {
        var plain = (await SlowLines(1, Plain, TimeSpan.FromMilliseconds(40))).Should().ContainSingle().Subject;

        plain.Should().Contain("regex=False").And.Contain("count=False").And.Contain("unboundedSort=False");

        var sortedBeforeAGroup = (await SlowLines(1, SortedBeforeAGroup, TimeSpan.FromMilliseconds(40))).Should().ContainSingle().Subject;

        sortedBeforeAGroup.Should().Contain("unboundedSort=True", "a sort before a group orders every candidate row");

        var remote = (await SlowLines(1, Remote, TimeSpan.FromMilliseconds(40), new FakeRemoteClient())).Should().ContainSingle().Subject;

        remote.Should().Contain("remote=True");
    }

    [Fact]
    public async Task A_request_under_the_threshold_is_not_logged_and_zero_turns_the_line_off()
    {
        (await SlowLines(600_000, Regex, TimeSpan.Zero)).Should().BeEmpty();
        (await SlowLines(0, Regex, TimeSpan.FromMilliseconds(40))).Should().BeEmpty();
        (await SlowLines(-5, Regex, TimeSpan.FromMilliseconds(40))).Should().BeEmpty("a negative threshold is off, not always-on");
    }

    [Fact]
    public void The_threshold_defaults_to_one_second_and_never_goes_negative()
    {
        new ExecutionOptions().SlowQueryMs.Should().Be(1_000);
        new ExecutionOptions().EffectiveSlowQueryMs.Should().Be(1_000);
        new ExecutionOptions { SlowQueryMs = -1 }.EffectiveSlowQueryMs.Should().Be(0);
        new ExecutionOptions().AllowDiskUse.Should().BeTrue("a sort over the server's memory limit spills instead of failing");
    }

    /// <summary>A correlation id a caller wrote: long, with a line break in it.</summary>
    private sealed class ForgedScope : IOxQLScopeProvider
    {
        public const string Forged = "corr\r\nforged line " + "xxxxxxxxxx";

        public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult<Guid?>(BindHost.Organisation);

        public string? CorrelationId(HttpContext? httpContext) => Forged + new string('y', 2_000);
    }

    [Fact]
    public async Task The_correlation_id_every_engine_line_reads_is_bounded_where_the_context_is_built()
    {
        var options = BindHost.Options();
        var models = new StaticEntityModelProvider(BindHost.Probe);
        var service = new OxQLQueryService(new MongoQueryEngine(models, new FakeAggregateRunner(), BindHost.Cursors, options), new ForgedScope(), options, models, EmptyAddonDefinitionSource.Instance);

        var context = await service.ContextAsync(null, CancellationToken.None);

        context.CorrelationId.Should().NotContainAny("\r", "\n");
        context.CorrelationId!.Length.Should().BeLessThan(300);
        context.CorrelationId.Should().StartWith("corr  forged line");
    }
}
