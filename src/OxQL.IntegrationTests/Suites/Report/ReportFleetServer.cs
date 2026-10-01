using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The fleet over HTTP, for the studio's acceptance specs to record the answers to the explains their
/// click paths send, one answer per click, in one run. Opt-in: it runs only when
/// <c>OXQL_SERVE_FLEET</c> names a port, listens on <c>http://localhost:&lt;port&gt;/</c> and ends on
/// <c>GET /stop</c> or after <see cref="ReportFleetServer.Lifetime"/>.
/// <code>
/// OXQL_SERVE_FLEET=5810 dotnet test src/OxQL.IntegrationTests --filter "FullyQualifiedName~Suites.Report.ReportFleetServerTests"
/// </code>
/// <list type="bullet">
/// <item><c>POST /&lt;service&gt;/explain</c>: the body sent to the service's <c>OxQL/explain</c> as organisation R under
/// contract 2; the answer is <c>{ status, answer }</c> with the body normalised as a golden answer is
/// (<see cref="Explain.ExplainGolden.Normalise"/>).</item>
/// <item><c>POST /&lt;service&gt;/query</c>: the same for <c>OxQL/query</c>, the body as the host answered it.</item>
/// <item><c>GET /stop</c>: ends the run.</item>
/// </list>
/// </summary>
[Trait("Category", "Integration")]
public class ReportFleetServerTests(ITestOutputHelper output)
{
    [ServeFact]
    public async Task Serve_the_fleet_until_stopped()
    {
        var port = int.Parse(Environment.GetEnvironmentVariable(ReportFleetServer.Variable)!, System.Globalization.CultureInfo.InvariantCulture);
        var served = await ReportFleetServer.RunAsync(port);

        output.WriteLine($"answered {served} requests");
    }
}

/// <summary>A fact that runs only when <see cref="ReportFleetServer.Variable"/> is set.</summary>
internal sealed class ServeFactAttribute : FactAttribute
{
    public ServeFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ReportFleetServer.Variable)))
            Skip = $"Opt-in: set {ReportFleetServer.Variable} to a port to serve the fleet for the studio's recorder.";
    }
}

/// <summary>The listener behind <see cref="ReportFleetServerTests"/>.</summary>
internal static class ReportFleetServer
{
    public const string Variable = "OXQL_SERVE_FLEET";

    /// <summary>How long the fleet is served when nothing stops it.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(45);

    /// <summary>Serves until <c>GET /stop</c> or the lifetime ends; the number of requests answered.</summary>
    public static async Task<int> RunAsync(int port)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        using var lifetime = new CancellationTokenSource(Lifetime);
        var served = 0;

        while (!lifetime.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await listener.GetContextAsync().WaitAsync(lifetime.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var segments = context.Request.Url!.AbsolutePath.Trim('/').Split('/');

            if (context.Request.HttpMethod == "GET" && segments is ["stop"])
            {
                await WriteAsync(context, 200, new JsonObject { ["stopped"] = true });
                break;
            }

            try
            {
                await WriteAsync(context, 200, await AnswerAsync(context, segments));
                served++;
            }
            catch (Exception error)
            {
                await WriteAsync(context, 500, new JsonObject { ["error"] = error.Message });
            }
        }

        listener.Stop();
        return served;
    }

    private static async Task<JsonObject> AnswerAsync(HttpListenerContext context, string[] segments)
    {
        if (context.Request.HttpMethod != "POST" || segments is not [var key, var verb] || verb is not ("explain" or "query"))
            throw new InvalidOperationException("Expected POST /<service>/explain or POST /<service>/query.");

        var service = LabService.All.SingleOrDefault(candidate => candidate.Key == key) ?? throw new InvalidOperationException($"The fleet has no service '{key}'.");

        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        var body = JsonNode.Parse(await reader.ReadToEndAsync())!;
        var client = await Lab.ClientAsync(service, Org.R, 2);
        var answer = verb == "explain" ? await client.ExplainHereAsync(body) : await client.QueryAsync(body);

        return new JsonObject
        {
            ["status"] = answer.StatusCode,
            ["answer"] = answer.Body is null ? null : verb == "explain" && answer.StatusCode == 200 ? Explain.ExplainGolden.Normalise(answer.Body.DeepClone()) : answer.Body.DeepClone(),
        };
    }

    private static async Task WriteAsync(HttpListenerContext context, int status, JsonObject body)
    {
        var bytes = Encoding.UTF8.GetBytes(body.ToJsonString());

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }
}
