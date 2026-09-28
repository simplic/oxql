using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// <c>POST /oxql/explain</c> over the client's own host, exactly as the studio calls it: the route,
/// its MVC output formatter and the client's organisation, user and contract headers. The answers
/// of <c>flatten</c> and chain scenarios nest deeper than MVC's default depth of 32, which the host
/// raises to <see cref="OxQL.Core.Models.OxQLJson.MaxDepth"/>; this helper once bypassed the route
/// for that reason and no longer does.
/// </summary>
internal static class ReportExplain
{
    public static Task<WireAnswer> ExplainAsync(LabClient client, JsonObject body) => client.ExplainHereAsync(body);
}
