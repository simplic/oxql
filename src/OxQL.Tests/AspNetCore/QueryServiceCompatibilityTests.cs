using FluentAssertions;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Batch;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// <see cref="IOxQLQueryService"/> grew in 2.1 without breaking what was written against 2.0: the
/// internal-call overloads default to the public form and refuse an internal call, and the 2.0
/// <c>ExplainAsync(QueryRequest)</c> is still there and explains the plain query.
/// </summary>
public class QueryServiceCompatibilityTests
{
    /// <summary>A service written against the members 2.0 had, with explain taking the 2.1 request.</summary>
    private sealed class PublicOnlyService : IOxQLQueryService
    {
        public List<string> Calls { get; } = [];

        public Task<QueryOutcome> ExecuteAsync(QueryRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<QueryOutcome> ExecuteAsync(QueryRequest request, int? maxTimeMs, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BatchOutcome> BatchAsync(BatchRequest batch, CancellationToken cancellationToken = default)
        {
            Calls.Add("batch");
            return Task.FromResult<BatchOutcome>(new BatchOutcome.Success(new BatchResponse { Results = [] }));
        }

        public Task<ExplainOutcome> ExplainAsync(ExplainRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add("explain:" + request.Query.EntityType + ":" + request.IsEnvelope);
            return Task.FromResult<ExplainOutcome>(new ExplainOutcome.Refused(Refusal.NotExecutable("X", "x")));
        }
    }

    [Fact]
    public async Task The_internal_call_overloads_serve_the_public_form_and_refuse_an_internal_call()
    {
        IOxQLQueryService service = new PublicOnlyService();
        var batch = new BatchRequest { Queries = [] };
        var explain = new ExplainRequest { Query = new QueryRequest { EntityType = "a.b", Pipeline = [] } };

        await service.BatchAsync(batch, internalCall: false);
        await service.ExplainAsync(explain, internalCall: false);

        ((PublicOnlyService)service).Calls.Should().Equal("batch", "explain:a.b:False");
        await FluentActions.Awaiting(() => service.BatchAsync(batch, internalCall: true)).Should().ThrowAsync<NotSupportedException>();
        await FluentActions.Awaiting(() => service.ExplainAsync(explain, internalCall: true)).Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task The_2_0_explain_of_a_plain_query_is_kept()
    {
        IOxQLQueryService service = new PublicOnlyService();

        await service.ExplainAsync(new QueryRequest { EntityType = "a.b", Pipeline = [] });

        ((PublicOnlyService)service).Calls.Should().Equal("explain:a.b:False");
    }
}
