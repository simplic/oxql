using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OxQL.AspNetCore;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Model.Build;
using OxQL.Mongo;

namespace OxQL.IntegrationTests.Suites.Entity;

/// <summary>
/// A wave-local host for area B: a lab service whose model declares retired entity ids, over the
/// shared fleet's database of that service (read only). The frozen fleet builds every service
/// model without retired ids (<see cref="LabService"/> offers no seam for them), so this host
/// registers the engine the way <see cref="FleetHost"/> does, with the one difference that the
/// model is built with <c>retiredIds</c>, as a service's schema options declare them.
/// <para>
/// The retired ids mirror the legacy fleet's published aliases: <c>department</c> and
/// <c>equipment</c> on the fleet service, <c>transaction</c> on the ledger service. The hosts
/// start on first use and live until the process exits, like the shared fleet's.
/// </para>
/// </summary>
internal static class RetiredIdHost
{
    /// <summary>Current entity id to the ids it retired, per lab service.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Retired =
        new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>
        {
            [LabService.Fleet.Key] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["fleet.department"] = ["department"],
                ["fleet.equipment"] = ["equipment"],
            },
            [LabService.Ledger.Key] = new Dictionary<string, IReadOnlyList<string>>
            {
                ["ledger.transaction"] = ["transaction"],
            },
        };

    private static readonly Dictionary<string, Lazy<Task<TestServer>>> Hosts = LabService.All
        .Where(service => Retired.ContainsKey(service.Key))
        .ToDictionary(service => service.Key, service => new Lazy<Task<TestServer>>(() => StartAsync(service), LazyThreadSafetyMode.ExecutionAndPublication));

    /// <summary>The host of a service with retired ids declared.</summary>
    public static Task<TestServer> ServerAsync(LabService service) => Hosts[service.Key].Value;

    /// <summary>
    /// Posts a query to the host as organisation A, under <paramref name="contract"/> (null sends
    /// no contract header), and reads the answer as <see cref="LabClient"/> does.
    /// </summary>
    public static async Task<WireAnswer> SendAsync(LabService service, string entityType, string pipeline, int? contract = 2)
    {
        var server = await ServerAsync(service);
        using var http = server.CreateClient();
        var body = Json.Request(entityType, pipeline).ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, "OxQL/query") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        if (contract is not null)
            request.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, contract.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        request.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, LabIdentity.OrganisationA.ToString("D"));
        request.Headers.TryAddWithoutValidation(LabIdentity.UserHeader, LabIdentity.User.ToString("D"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();

        return new WireAnswer(response.StatusCode, Json.TryParse(text), text, new Dictionary<string, string>(), watch.Elapsed, $"POST OxQL/query {body}");
    }

    private static EntityModel Model(LabService service)
    {
        var findings = new List<BuildFinding>();
        var declarations = EntityScanner.Scan([typeof(LabService).Assembly], findings)
            .Where(declaration => declaration.Id.StartsWith(service.Key + ".", StringComparison.Ordinal))
            .ToList();

        return ClrModelBuilder.Build(declarations, Retired[service.Key]);
    }

    private static async Task<TestServer> StartAsync(LabService service)
    {
        var shared = await CorpusFleet.SharedAsync();
        var database = await shared.Fleet.DatabaseAsync(service);
        var client = await MongoFixture.ClientAsync();
        var model = Model(service);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(RetiredIdHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("OxQL:Cursor:SigningKey", shared.Fleet.SigningKey)]);
        builder.Logging.ClearProviders();

        var services = builder.Services;
        services.AddScoped<IAddonDefinitionSource>(_ => new TestAddonSource(database));
        services.AddOxQLCore(builder.Configuration.GetSection("OxQL"));
        services.AddSingleton<IEntityModelProvider>(new StaticEntityModelProvider(model));
        services.AddSingleton(client);
        services.AddOxQLMongo(options =>
        {
            options.DatabaseName = database.DatabaseNamespace.DatabaseName;
            options.IncludeErrorDetails = true;
        });
        services.AddOxQLAspNetCore(options => options.ContinuousIntegration = false);
        services.AddOxQLScope<TestScopeProvider>();
        services.AddSingleton(shared.Fleet);
        services.AddSingleton<IRemoteQueryClient, InMemoryRemoteClient>();

        var app = builder.Build();
        app.MapControllers();
        await app.StartAsync();

        return app.GetTestServer();
    }
}
