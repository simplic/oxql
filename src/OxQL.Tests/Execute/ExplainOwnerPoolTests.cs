using FluentAssertions;
using OxQL.Core.Engine;
using OxQL.Model.Addon;
using OxQL.Mongo;
using OxQL.Mongo.Explain;
using OxQL.Tests.Bind;
using Xunit;

namespace OxQL.Tests.Execute;

/// <summary>
/// The turn of an <see cref="ExplainOwnerPool"/> under the caller's cancellation: the explains of a
/// pool compute one at a time, and one that waits for its turn when its caller gives up leaves the
/// queue then, without taking the turn and binding for a caller that is gone.
/// </summary>
public class ExplainOwnerPoolTests
{
    [Fact]
    public async Task An_explain_waiting_for_its_turn_leaves_when_its_caller_gives_up_and_never_runs()
    {
        var pool = new ExplainOwnerPool(explains: 2, calls: 8);
        var hold = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        var ran = false;

        var first = pool.RunAsync(() => hold.Task, CancellationToken.None);
        var second = pool.RunAsync(() =>
        {
            ran = true;

            return Task.FromResult(2);
        }, caller.Token);

        await caller.CancelAsync();

        await FluentActions.Awaiting(() => second).Should().ThrowAsync<OperationCanceledException>();
        ran.Should().BeFalse("it left the queue; it did not take its turn first");

        hold.SetResult(1);
        (await first).Should().Be(1);

        // The turn the first one held is free again: the one that left gave none back that it never took.
        (await pool.RunAsync(() => Task.FromResult(3), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5))).Should().Be(3);
    }

    /// <summary>An addon source that counts what it is asked, so a check that binds shows.</summary>
    private sealed class CountingAddons : IAddonDefinitionSource
    {
        public int Asked { get; private set; }

        public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken)
        {
            Asked++;

            return ValueTask.FromResult<IReadOnlyList<AddonDefinition>>([]);
        }
    }

    [Fact]
    public async Task An_internal_explain_batch_whose_caller_is_gone_binds_none_of_its_checks()
    {
        var options = BindHost.Options();
        var addons = new CountingAddons();
        var engine = new MongoQueryEngine(new StaticEntityModelProvider(BindHost.Probe), new FakeAggregateRunner(), BindHost.Cursors, options);
        var checks = Enumerable.Range(0, 5).Select(_ => new ExplainRequest { Query = BindHost.Request("probe.order", "[]") }).ToList();
        using var caller = new CancellationTokenSource();

        await caller.CancelAsync();

        await FluentActions.Awaiting(() => engine.ExplainBatchAsync(checks, BindHost.Context(options, addons: addons), null, caller.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        addons.Asked.Should().Be(0, "no check took a turn to bind for a caller that is gone");
    }
}
