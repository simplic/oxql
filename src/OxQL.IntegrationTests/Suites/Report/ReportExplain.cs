using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// <c>POST /oxql/explain</c> as the controller answers it, minus the MVC output formatter: the
/// client's host's query service (<see cref="IOxQLQueryService.ExplainAsync(ExplainRequest, CancellationToken)"/>,
/// the public overload the route calls) in a request scope of that host carrying the client's
/// organisation, user and contract headers, and the answer written with the engine's wire options.
/// <para>
/// The route itself cannot carry these answers: MVC's JSON options stop at depth 32, and the
/// emitted page stages of a <c>flatten</c> unwind (A1, A2b, A4, A5) nest deeper, so the host fails
/// while writing the 200 (E14a open issue). Everything else is the route's: the same service, the
/// same scope provider, the same remote client with the forwarded identity.
/// </para>
/// </summary>
internal static class ReportExplain
{
    public static async Task<WireAnswer> ExplainAsync(LabClient client, JsonObject body)
    {
        var host = await client.HostAsync();
        var text = body.ToJsonString();
        var request = JsonSerializer.Deserialize<ExplainRequest>(text, OxQLJson.Wire)!;
        var watch = Stopwatch.StartNew();

        await using var scope = host.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };

        if (client.Who.Contract is { } contract)
            context.Request.Headers[LabIdentity.ContractHeader] = contract.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (client.Who.Organisation is { } organisation)
            context.Request.Headers[LabIdentity.OrganisationHeader] = organisation.ToString("D");

        if (client.Who.User is { } user)
            context.Request.Headers[LabIdentity.UserHeader] = user.ToString("D");

        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = context;

        try
        {
            var (status, answer) = await scope.ServiceProvider.GetRequiredService<IOxQLQueryService>().ExplainAsync(request) switch
            {
                ExplainOutcome.Success success => (HttpStatusCode.OK, JsonSerializer.SerializeToNode(success.Result, OxQLJson.Wire)),
                ExplainOutcome.Refused refused => ((HttpStatusCode)refused.Refusal.Status, JsonSerializer.SerializeToNode(refused.Refusal, OxQLJson.Wire)),
                _ => throw new InvalidOperationException("An unknown explain outcome."),
            };

            return new WireAnswer(status, answer, answer?.ToJsonString() ?? "", new Dictionary<string, string>(), watch.Elapsed, $"explain (in process) {text}");
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }
}
