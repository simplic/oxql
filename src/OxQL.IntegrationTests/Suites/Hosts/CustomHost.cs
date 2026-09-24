using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;
using OxQL.AspNetCore;
using OxQL.AspNetCore.Scope;
using OxQL.Core;
using OxQL.Core.Engine;
using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;
using OxQL.IntegrationTests.Harness;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Mongo;

namespace OxQL.IntegrationTests.Suites.Hosts;

/// <summary>
/// A service host wired differently from the fleet's, for the cases whose subject is the wiring
/// itself: a host with no remote query client, a host with no scope provider, a scope provider
/// or addon source that throws, a model built with retired entity ids. It registers what
/// <see cref="FleetHost"/> registers, in the same order, and leaves out or replaces exactly what
/// <see cref="Wiring"/> says. It reads a database it is handed and never writes to it.
/// <para>
/// A local helper of the B5 suites: <see cref="FleetHost"/> always installs the test scope
/// provider and the in-memory remote client, and the fleet is frozen, so a host without them is
/// built here.
/// </para>
/// </summary>
public sealed class CustomHost : IAsyncDisposable
{
    private readonly WebApplication app;

    private CustomHost(WebApplication app)
    {
        this.app = app;
        Server = app.GetTestServer();
    }

    public TestServer Server { get; }

    public IServiceProvider Services => app.Services;

    /// <summary>How the host differs from a fleet host.</summary>
    public sealed record Wiring
    {
        /// <summary>The model; the service's own when null.</summary>
        public EntityModel? Model { get; init; }

        /// <summary>The scope provider; the test provider when null, none at all when <see cref="NoScope"/>.</summary>
        public IOxQLScopeProvider? Scope { get; init; }

        /// <summary>Registers no scope provider at all.</summary>
        public bool NoScope { get; init; }

        /// <summary>The addon source; the test source over the database when null.</summary>
        public IAddonDefinitionSource? Addons { get; init; }

        /// <summary>Configuration layered over the defaults.</summary>
        public IReadOnlyDictionary<string, string?>? Configuration { get; init; }
    }

    /// <summary>
    /// Starts a host for <paramref name="service"/> over <paramref name="database"/>. No remote
    /// query client is registered: a remote reference has nothing to call.
    /// </summary>
    public static async Task<CustomHost> StartAsync(LabService service, IMongoDatabase database, Wiring? wiring = null)
    {
        wiring ??= new Wiring();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development",
            ApplicationName = typeof(CustomHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["OxQL:Cursor:SigningKey"] = Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray()) }
                .Concat(wiring.Configuration ?? new Dictionary<string, string?>()));
        builder.Logging.ClearProviders();

        var services = builder.Services;

        services.AddScoped(_ => wiring.Addons ?? new TestAddonSource(database));
        services.AddOxQLCore(builder.Configuration.GetSection("OxQL"));

        var model = wiring.Model;
        services.AddSingleton<IEntityModelProvider>(new LazyEntityModelProvider(() => model ?? service.Model));
        services.AddSingleton(database.Client);
        services.AddOxQLMongo(options =>
        {
            options.DatabaseName = database.DatabaseNamespace.DatabaseName;
            options.IncludeErrorDetails = true;
        });

        services.AddOxQLAspNetCore(options => options.ContinuousIntegration = false);

        if (wiring.Scope is { } scope)
            services.AddScoped(_ => scope);
        else if (!wiring.NoScope)
            services.AddOxQLScope<TestScopeProvider>();

        var app = builder.Build();
        app.MapControllers();

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        return new CustomHost(app);
    }

    /// <summary>The shared fleet's database of a service: read it, never write it.</summary>
    public static async Task<IMongoDatabase> SharedDatabaseAsync(LabService service) =>
        await (await CorpusFleet.SharedAsync()).Fleet.DatabaseAsync(service);

    /// <summary>A POST as <paramref name="organisation"/> (none when null) under contract 2.</summary>
    public async Task<WireAnswer> PostAsync(string route, string body, Guid? organisation, int? contract = 2)
    {
        using var client = Server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, route) { Content = new StringContent(body, Encoding.UTF8) };

        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");

        if (contract is { } number)
            request.Headers.TryAddWithoutValidation(LabIdentity.ContractHeader, number.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (organisation is { } id)
            request.Headers.TryAddWithoutValidation(LabIdentity.OrganisationHeader, id.ToString("D"));

        request.Headers.TryAddWithoutValidation(LabIdentity.CorrelationHeader, "b5-correlation");

        var watch = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.Concat(response.Content.Headers).ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase);

        return new WireAnswer(response.StatusCode, Json.TryParse(text), text, headers, watch.Elapsed, $"POST {route} {body}");
    }

    /// <summary><c>POST OxQL/query</c> for one entity as organisation A.</summary>
    public Task<WireAnswer> SendAsync(string entityType, string pipeline, Org org = Org.A) =>
        PostAsync("OxQL/query", Json.Request(entityType, pipeline).ToJsonString(), org.Id());

    /// <summary>A raw GET.</summary>
    public async Task<WireAnswer> GetAsync(string route)
    {
        using var client = Server.CreateClient();
        using var response = await client.GetAsync(route);
        var text = await response.Content.ReadAsStringAsync();

        return new WireAnswer(response.StatusCode, Json.TryParse(text), text, new Dictionary<string, string>(), TimeSpan.Zero, $"GET {route}");
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

/// <summary>A scope provider whose organisation lookup fails, as a token service that is down does.</summary>
public sealed class ThrowingScopeProvider : IOxQLScopeProvider
{
    public ValueTask<Guid?> OrganisationAsync(HttpContext? httpContext, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The organisation lookup is down.");

    public string? UserId(HttpContext? httpContext) => null;

    public string? CorrelationId(HttpContext? httpContext) =>
        httpContext?.Request.Headers[LabIdentity.CorrelationHeader].FirstOrDefault();
}

/// <summary>An addon definition source whose repository is down.</summary>
public sealed class ThrowingAddonSource : IAddonDefinitionSource
{
    public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
        throw new TimeoutException("The addon definition repository did not answer.");
}
