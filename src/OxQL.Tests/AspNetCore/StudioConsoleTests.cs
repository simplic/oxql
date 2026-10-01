using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OxQL.Core.Models;
using OxQL.Studio;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// The console's routes (DESIGN §7): the shell and the assets sit at <c>RoutePath</c> relative to
/// the path base, load without a credential, never shadow the API's own routes, and the console
/// warns when its Explain switch and the engine's differ.
/// </summary>
public class StudioConsoleTests
{
    private const string PathBase = "/svc-api/v1";

    /// <summary>A host with only the console mapped, behind a path base.</summary>
    private static async Task<WebApplication> Host(
        Action<OxQLStudioOptions>? studio = null,
        OxQLOptions? engine = null,
        LogCapture? logs = null,
        bool requireAuthentication = false,
        Action<WebApplication>? map = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        if (logs is not null)
            builder.Logging.AddProvider(logs);

        builder.Services.AddOxQLStudio(studio);
        if (engine is not null)
            builder.Services.AddSingleton(engine);

        if (requireAuthentication)
        {
            builder.Services.AddAuthentication(RefusingHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, RefusingHandler>(RefusingHandler.SchemeName, _ => { });
            builder.Services.AddAuthorization(options =>
                options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        }

        var app = builder.Build();
        app.UsePathBase(PathBase);
        app.UseRouting();
        if (requireAuthentication)
        {
            app.UseAuthentication();
            app.UseAuthorization();
        }

        map?.Invoke(app);
        app.MapOxQLStudio();
        await app.StartAsync();
        return app;
    }

    /// <summary>Authenticates nobody, so every endpoint that is not anonymous answers 401.</summary>
    private sealed class RefusingHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "refusing";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private static JsonElement ConfigOf(string html)
    {
        var match = Regex.Match(html, "<script id=\"oxql-config\" type=\"application/json\">(.*?)</script>", RegexOptions.Singleline);
        match.Success.Should().BeTrue();
        return JsonDocument.Parse(match.Groups[1].Value).RootElement;
    }

    [Fact]
    public async Task The_shell_and_assets_are_mapped_at_the_route_path_under_the_path_base()
    {
        await using var app = await Host(studio: options =>
        {
            options.RoutePath = "/tools/console";
            options.ApiBasePath = "/oxql";
            options.StudioAppUrl = "https://studio.example/oxql";
        });
        var client = app.GetTestClient();

        var shell = await client.GetAsync($"{PathBase}/tools/console");
        shell.StatusCode.Should().Be(HttpStatusCode.OK);
        shell.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var html = await shell.Content.ReadAsStringAsync();
        html.Should().Contain($"href=\"{PathBase}/tools/console/styles.css\"")
            .And.Contain($"src=\"{PathBase}/tools/console/app.js\"")
            .And.NotContain("__OXQL_");

        var config = ConfigOf(html);
        config.GetProperty("assetBasePath").GetString().Should().Be($"{PathBase}/tools/console");
        config.GetProperty("apiBasePath").GetString().Should().Be($"{PathBase}/oxql");
        config.GetProperty("schemaBasePath").GetString().Should().Be($"{PathBase}/schema");
        config.GetProperty("enableExplain").GetBoolean().Should().BeTrue();
        config.GetProperty("studioAppUrl").GetString().Should().Be("https://studio.example/oxql");

        (await client.GetAsync($"{PathBase}/oxql")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the shell no longer sits at a hard-coded /oxql");
    }

    [Theory]
    [InlineData("app.js", "text/javascript")]
    [InlineData("styles.css", "text/css")]
    public async Task Assets_are_served_beside_the_shell(string asset, string mediaType)
    {
        await using var app = await Host(studio: options => options.RoutePath = "/tools/console");
        var client = app.GetTestClient();

        var response = await client.GetAsync($"{PathBase}/tools/console/{asset}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be(mediaType);
        (await response.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("oxql-lang.js")]
    [InlineData("index.html")]
    [InlineData("missing.js")]
    public async Task Removed_unknown_and_page_assets_are_not_served(string asset)
    {
        await using var app = await Host();
        var client = app.GetTestClient();

        (await client.GetAsync($"{PathBase}/oxql/{asset}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_default_route_path_is_oxql_relative_to_the_path_base()
    {
        await using var app = await Host();
        var client = app.GetTestClient();

        (await client.GetAsync($"{PathBase}/oxql")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{PathBase}/oxql/app.js")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_console_loads_without_a_credential_on_a_host_that_requires_one()
    {
        await using var app = await Host(requireAuthentication: true, map: host =>
            host.MapGet("/protected", () => "secret"));
        var client = app.GetTestClient();

        (await client.GetAsync($"{PathBase}/protected")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the fallback policy is in force");
        (await client.GetAsync($"{PathBase}/oxql")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{PathBase}/oxql/app.js")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync($"{PathBase}/oxql/styles.css")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_api_routes_beside_the_console_are_never_taken_for_assets()
    {
        // OxS maps the console and the API at the same "/oxql".
        await using var app = await Host(map: host =>
        {
            host.MapGet("/oxql/health", () => Results.Json(new { status = "healthy" }));
            host.MapGet("/oxql/query", () => "query");
        });
        var client = app.GetTestClient();

        var health = await client.GetAsync($"{PathBase}/oxql/health");
        health.StatusCode.Should().Be(HttpStatusCode.OK);
        (await health.Content.ReadAsStringAsync()).Should().Contain("healthy");
        (await client.GetStringAsync($"{PathBase}/oxql/query")).Should().Be("query");

        foreach (var reserved in OxQLStudioEndpointExtensions.ReservedAssetNames)
        {
            (await client.GetAsync($"{PathBase}/oxql/{reserved}.js")).StatusCode.Should().Be(HttpStatusCode.NotFound, $"'{reserved}' is a reserved asset name");
        }
    }

    [Fact]
    public void No_shipped_asset_uses_a_reserved_name()
    {
        var assets = typeof(OxQLStudioOptions).Assembly.GetManifestResourceNames()
            .Select(name => name[(name.IndexOf(".wwwroot.", StringComparison.Ordinal) + ".wwwroot.".Length)..])
            .ToList();

        assets.Should().BeEquivalentTo(["index.html", "app.js", "styles.css"]);
        assets.Select(Path.GetFileNameWithoutExtension)
            .Should().NotIntersectWith(OxQLStudioEndpointExtensions.ReservedAssetNames);
        OxQLStudioEndpointExtensions.ReservedAssetNames.Should().BeEquivalentTo(["health", "query", "batch", "explain"]);
    }

    [Fact]
    public void The_console_asks_explain_for_the_plan_and_reads_the_answer_it_gets()
    {
        var assembly = typeof(OxQLStudioOptions).Assembly;
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(name => name.EndsWith(".wwwroot.app.js", StringComparison.Ordinal)))!;
        using var reader = new StreamReader(stream);
        var script = reader.ReadToEnd();

        script.Should().Contain("""["notes", "plan", "indexes"]""", "the plan and the advisory are opt-in, and the console shows both");
        script.Should().Contain("answer.plan").And.Contain("plan.stages").And.Contain("plan.count").And.Contain("answer.advisory");
        script.Should().Contain("answer.stages").And.Contain("s.placement?.executor").And.Contain("answer.owners");
        script.Should().NotContain("answer.steps").And.NotContain("json.steps").And.NotContain("answer.bound").And.NotContain("describe", "the former answer and its describe are gone");
        script.Should().Contain("res.status === 429", "an explain over the rate says when to retry");
    }

    [Fact]
    public async Task The_console_defaults_agree_with_the_engine_and_log_no_warning()
    {
        var logs = new LogCapture();
        await using var app = await Host(engine: new OxQLOptions(), logs: logs);

        new OxQLOptions().Explain.Enabled.Should().BeTrue();
        new OxQLStudioOptions().EnableExplain.Should().BeTrue();
        logs.Entries.Where(entry => entry.Level == LogLevel.Warning).Should().BeEmpty();
    }

    [Fact]
    public async Task A_console_switch_that_differs_from_the_engine_is_logged_as_a_warning()
    {
        var logs = new LogCapture();
        await using var app = await Host(studio: options => options.EnableExplain = false, engine: new OxQLOptions(), logs: logs);

        logs.Entries.Should().Contain(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("differ"));
    }
}
