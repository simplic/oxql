using System.Net;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Controllers;
using OxQL.AspNetCore.OpenApi;
using Xunit;

namespace OxQL.Tests.AspNetCore;

/// <summary>
/// What the OxQL routes say of themselves to an API description, so that a document generated
/// from them is the contract 2 surface and cannot drift from it: every route has a summary and a
/// description that points to the documentation, the contract header as a parameter, the body
/// model of the wire, and every status with its body. The first half reads the API explorer, as any
/// generator does; the second reads the document the Sample host's generator writes from it.
/// </summary>
public class ApiDescriptionTests
{
    private static IReadOnlyList<ApiDescription> Routes(SampleHost host) =>
        host.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(group => group.Items)
            .Where(description => description.RelativePath is { } path && path.StartsWith("OxQL/", StringComparison.Ordinal))
            .ToList();

    private static ApiDescription Route(SampleHost host, string method, string path) =>
        Routes(host).Single(description => description.HttpMethod == method && description.RelativePath == path);

    public static TheoryData<string, string, string, string, Type?, int[]> Described => new()
    {
        // method, path, summary, description, the body model, the statuses
        { "POST", "OxQL/query", OxQLDocumentation.QuerySummary, OxQLDocumentation.QueryDescription, typeof(OxQLQueryBody), [200, 400, 401, 403, 413, 422, 500, 504] },
        { "POST", "OxQL/batch", OxQLDocumentation.BatchSummary, OxQLDocumentation.BatchDescription, typeof(OxQLBatchBody), [200, 400, 401, 413, 500] },
        { "POST", "OxQL/explain", OxQLDocumentation.ExplainSummary, OxQLDocumentation.ExplainDescription, typeof(OxQLExplainBody), [200, 304, 400, 401, 403, 404, 413, 429, 500] },
        { "GET", "OxQL/health", OxQLDocumentation.HealthSummary, OxQLDocumentation.HealthDescription, null, [200] },
    };

    [Fact]
    public void The_explorer_lists_the_four_routes_and_no_other()
    {
        using var host = new SampleHost();

        Routes(host).Select(description => $"{description.HttpMethod} {description.RelativePath}").Order(StringComparer.Ordinal)
            .Should().Equal("GET OxQL/health", "POST OxQL/batch", "POST OxQL/explain", "POST OxQL/query");
    }

    [Theory]
    [MemberData(nameof(Described))]
    public void Every_route_has_a_summary_a_description_the_contract_header_its_body_model_and_its_statuses(string method, string path, string summary, string description, Type? body, int[] statuses)
    {
        using var host = new SampleHost();
        var route = Route(host, method, path);
        var metadata = route.ActionDescriptor.EndpointMetadata;

        // One sentence, and a description that points to the documentation instead of repeating it.
        metadata.OfType<IEndpointSummaryMetadata>().Last().Summary.Should().Be(summary).And.EndWith(".").And.NotContain("\n");
        summary.Length.Should().BeLessThan(90);
        metadata.OfType<IEndpointDescriptionMetadata>().Last().Description.Should().Be(description).And.Contain(OxQLDocumentation.Url).And.Contain("X-OxQL-Contract");
        description.Length.Should().BeLessThan(800, "a description says what the route is for and where the language is described");

        foreach (var ofTheLanguage in new[] { "startsWith", "$var", "dateTrunc", "\"eq\"", "| " })
            description.Should().NotContain(ofTheLanguage, "the language is documented in one place, and that is not the API description");

        // The contract header, optional and from the header.
        var contract = route.ParameterDescriptions.Should().ContainSingle(parameter => parameter.Name == "X-OxQL-Contract").Subject;

        contract.Source.Should().Be(BindingSource.Header);
        contract.IsRequired.Should().BeFalse("no header is contract 1");
        contract.Type.Should().Be(typeof(string));
        contract.ModelMetadata.ContainerType.Should().Be(typeof(OxQLRequestHeaders));

        // The body as it is written on the wire, not as the engine binds it.
        var bodies = route.ParameterDescriptions.Where(parameter => parameter.Source == BindingSource.Body).ToList();

        if (body is null)
        {
            bodies.Should().BeEmpty();
        }
        else
        {
            var described = bodies.Should().ContainSingle().Subject;

            described.Type.Should().Be(body);
            described.ModelMetadata.ModelType.Should().Be(body);
            route.SupportedRequestFormats.Select(format => format.MediaType).Should().Contain("application/json");
        }

        // Every status the route answers, each with its body where it has one.
        route.SupportedResponseTypes.Select(response => response.StatusCode).Order().Should().Equal(statuses);

        foreach (var response in route.SupportedResponseTypes)
        {
            response.Description.Should().NotBeNullOrWhiteSpace($"{response.StatusCode} says what it means, without the comments of the assembly");

            if (response.StatusCode == 304)
            {
                (response.Type is null || response.Type == typeof(void)).Should().BeTrue("a 304 has no body");
                continue;
            }

            // What the host answers before the engine sees the request (no authentication, a route switched off) is the host's body, not the envelope.
            if (response.StatusCode is 401 or 404)
            {
                response.Type.Should().Be(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), $"{response.StatusCode} is the host's own answer");
                continue;
            }

            response.Type.Should().NotBeNull($"{response.StatusCode}");
            response.Type!.Namespace.Should().Be(typeof(OxQLError).Namespace, $"{response.StatusCode} is described by a model of the wire");
            response.ApiResponseFormats.Select(format => format.MediaType).Should().Equal(["application/json"], $"{response.StatusCode}");

            if (response.StatusCode >= 400)
                response.Type.Should().Be(typeof(OxQLError), $"{response.StatusCode} is a refusal");
        }
    }

    [Fact]
    public void The_answers_are_the_models_of_the_wire_and_health_is_anonymous_with_its_query_parameter()
    {
        using var host = new SampleHost();

        Type? Answer(string method, string path) => Route(host, method, path).SupportedResponseTypes.Single(response => response.StatusCode == 200).Type;

        Answer("POST", "OxQL/query").Should().Be(typeof(OxQLQueryAnswer));
        Answer("POST", "OxQL/batch").Should().Be(typeof(OxQLBatchAnswer));
        Answer("POST", "OxQL/explain").Should().Be(typeof(OxQLExplainAnswer));
        Answer("GET", "OxQL/health").Should().Be(typeof(OxQLHealthAnswer));

        var health = Route(host, "GET", "OxQL/health");

        health.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Should().NotBeEmpty("health is anonymous and says so");
        health.ParameterDescriptions.Should().ContainSingle(parameter => parameter.Name == "shallow" && parameter.Source == BindingSource.Query)
            .Which.Type.Should().Be(typeof(bool));
        health.ParameterDescriptions.Select(parameter => parameter.Name).Should().BeEquivalentTo(["shallow", "X-OxQL-Contract"], "what the action takes from its services is no parameter of the route");

        foreach (var route in new[] { Route(host, "POST", "OxQL/query"), Route(host, "POST", "OxQL/batch") })
        {
            route.ParameterDescriptions.Should().Contain(parameter => parameter.Name == "X-OxQL-Timing" && parameter.Source == BindingSource.Header && !parameter.IsRequired);
            route.ActionDescriptor.EndpointMetadata.OfType<IAllowAnonymous>().Should().BeEmpty();
        }

        // The contract 1 forms are named for a host that documents them beside the current ones.
        var query = Route(host, "POST", "OxQL/query").ActionDescriptor.EndpointMetadata.OfType<OxQLOperationAttribute>().Single();

        query.Contract1Body.Should().Be(typeof(OxQLContract1QueryBody));
        query.Contract1Answer.Should().Be(typeof(OxQLContract1QueryAnswer));
    }

    [Fact]
    public void The_models_name_the_members_of_the_wire()
    {
        static string[] Members(Type model) => model.GetProperties().Select(property => property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name).ToArray();

        Members(typeof(OxQLQueryBody)).Should().BeEquivalentTo(["entityType", "pipeline", "variables", "filter", "strict", "onMissing", "timing", "maxTimeMs"], "a request's members, without the one only the route between services takes");
        Members(typeof(OxQLStage)).Should().BeEquivalentTo(OxQL.Core.Models.PipelineStage.KnownKeys);
        Members(typeof(OxQLPageStage)).Should().BeEquivalentTo(["limit", "cursor", "count"], "a contract 2 page: no offset and no includeTotalCount");
        Members(typeof(OxQLBatchBody)).Should().BeEquivalentTo(["queries", "maxTimeMs"]);
        Members(typeof(OxQLExplainBody)).Should().BeEquivalentTo(["query", "include", "shape", "remote", "catalog"]);
        Members(typeof(OxQLError)).Should().BeEquivalentTo(["type", "title", "errors", "status"]);
        Members(typeof(OxQLErrorDetail)).Should().BeEquivalentTo(["code", "message", "stage", "filter", "path", "params", "basis"]);
        Members(typeof(OxQLQueryAnswer)).Should().BeEquivalentTo(["items", "pageInfo", "diagnostics", "timing"]);
        Members(typeof(OxQLPageInfo)).Should().BeEquivalentTo(["hasNextPage", "nextCursor", "count"]);
        Members(typeof(OxQLCount)).Should().BeEquivalentTo(["state", "value", "stoppedBy", "reason", "ms"]);

        // The error envelope and the answer are what the engine writes: member for member.
        static string[] Written(Type type) => type.GetProperties()
            .Where(property => property.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is not { Condition: System.Text.Json.Serialization.JsonIgnoreCondition.Always })
            .Select(property => property.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()?.Name)
            .Where(name => name is not null).Select(name => name!).ToArray();

        Members(typeof(OxQLError)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.Refusal)));
        Members(typeof(OxQLErrorDetail)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Models.QueryValidationError)));
        Members(typeof(OxQLDiagnostic)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.Diagnostic)));
        Members(typeof(OxQLCount)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.CountInfo)));
        Members(typeof(OxQLQueryAnswer)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.QueryResult)));
        Members(typeof(OxQLExplainAnswer)).Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.ExplainResult)));
        Members(typeof(OxQLPageInfo)).Concat(Members(typeof(OxQLContract1PageInfo))).Distinct().Should().BeEquivalentTo(Written(typeof(OxQL.Core.Engine.PageInfo)), "the two contracts' paging members together are the engine's");
    }

    [Fact]
    public void The_comments_that_ship_with_the_package_describe_every_route_parameter_and_member()
    {
        var assembly = typeof(OxQLController).Assembly;
        var file = Path.Combine(Path.GetDirectoryName(assembly.Location)!, assembly.GetName().Name + ".xml");

        File.Exists(file).Should().BeTrue("the XML comments are built beside the assembly and packed with it");

        var members = XDocument.Load(file).Descendants("member").ToDictionary(member => member.Attribute("name")!.Value, member => member);

        foreach (var action in new[] { "Query", "Batch", "Explain", "Health" })
        {
            var documented = members.Single(member => member.Key.StartsWith($"M:{typeof(OxQLController).FullName}.{action}(", StringComparison.Ordinal)).Value;
            var method = typeof(OxQLController).GetMethod(action)!;

            documented.Element("summary")!.Value.Trim().Should().NotBeNullOrEmpty().And.NotContain("\n", $"{action}: one sentence");
            documented.Element("remarks").Should().BeNull($"{action}: the description of a route is the one it carries as metadata, with the address of the documentation");
            documented.Elements("param").Select(parameter => parameter.Attribute("name")!.Value).Should().BeEquivalentTo(method.GetParameters().Select(parameter => parameter.Name));
            documented.Elements("response").Should().BeEmpty($"{action}: what a status means is said once, on the status itself");
            method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute>().Should().OnlyContain(response => !string.IsNullOrWhiteSpace(response.Description), $"{action}: every status is described");
        }

        // Every member of every model of the wire is described.
        foreach (var model in assembly.GetExportedTypes().Where(type => type.Namespace == typeof(OxQLError).Namespace && type.IsClass && type.Name.StartsWith("OxQL", StringComparison.Ordinal) && !typeof(Attribute).IsAssignableFrom(type)))
        {
            members.Should().ContainKey("T:" + model.FullName);

            foreach (var property in model.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                members.Should().ContainKey($"P:{model.FullName}.{property.Name}", "a member without a description is a member a reader has to guess");
        }
    }

    // ---- the document a generator writes from it -------------------------------------------------------

    private static async Task<JsonObject> DocumentAsync(SampleHost host)
    {
        var response = await host.CreateClient().GetAsync("/swagger/v1/swagger.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
    }

    private static string? Reference(JsonNode? schema) => schema?["$ref"]?.GetValue<string>().Split('/')[^1];

    [Fact]
    public async Task The_generated_document_describes_the_contract_2_surface()
    {
        using var host = new SampleHost();
        var document = await DocumentAsync(host);
        var schemas = document["components"]!["schemas"]!.AsObject();
        var query = document["paths"]!["/OxQL/query"]!["post"]!.AsObject();

        query["summary"]!.GetValue<string>().Should().Be(OxQLDocumentation.QuerySummary);
        query["description"]!.GetValue<string>().Should().Be(OxQLDocumentation.QueryDescription);

        var contract = query["parameters"]!.AsArray().Single(parameter => parameter!["name"]!.GetValue<string>() == "X-OxQL-Contract")!;

        contract["in"]!.GetValue<string>().Should().Be("header");
        (contract["required"]?.GetValue<bool>() ?? false).Should().BeFalse();
        contract["description"]!.GetValue<string>().Should().Contain("contract 1").And.Contain("`2`");
        query["parameters"]!.AsArray().Select(parameter => parameter!["name"]!.GetValue<string>()).Should().BeEquivalentTo(["X-OxQL-Contract", "X-OxQL-Timing"]);

        // The body and the answers by the models of the wire.
        Reference(query["requestBody"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLQueryBody));
        query["responses"]!.AsObject().Select(response => response.Key).Should().BeEquivalentTo(["200", "400", "401", "403", "413", "422", "500", "504"]);
        Reference(query["responses"]!["200"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLQueryAnswer));
        Reference(query["responses"]!["400"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLError));
        query["responses"]!["504"]!["description"]!.GetValue<string>().Should().Be("The query did not finish within its time.");
        Reference(query["responses"]!["401"]!["content"]!.AsObject().First().Value!["schema"]).Should().Be("ProblemDetails", "what a host answers without authentication is its own body, not the envelope");

        // A contract 2 page: a count, no offset, no includeTotalCount.
        schemas[nameof(OxQLPageStage)]!["properties"]!.AsObject().Select(property => property.Key).Should().BeEquivalentTo(["limit", "cursor", "count"]);
        schemas[nameof(OxQLStage)]!["properties"]!.AsObject().Select(property => property.Key).Should().BeEquivalentTo(OxQL.Core.Models.PipelineStage.KnownKeys);
        schemas[nameof(OxQLQueryBody)]!["properties"]!.AsObject().ContainsKey("keyedBy").Should().BeFalse("the member of the route between services is not published");
        schemas[nameof(OxQLQueryBody)]!["required"]!.AsArray().Select(name => name!.GetValue<string>()).Should().BeEquivalentTo(["entityType", "pipeline"]);
        schemas[nameof(OxQLPageInfo)]!["properties"]!["count"].Should().NotBeNull();
        schemas[nameof(OxQLErrorDetail)]!["properties"]!.AsObject().Select(property => property.Key).Should().BeEquivalentTo(["code", "message", "stage", "filter", "path", "params", "basis"]);
        schemas[nameof(OxQLQueryBody)]!["properties"]!["entityType"]!["description"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace("the comments of the engine's assembly describe the members");

        // The engine's own body models are not what the routes publish.
        schemas.Select(schema => schema.Key).Should().NotContain(["QueryRequest", "PageStage", "KeyedByMember", "BatchRequest", "ExplainRequest", "Refusal", "QueryResult"]);
    }

    [Fact]
    public async Task The_generated_document_describes_batch_explain_and_health()
    {
        using var host = new SampleHost();
        var document = await DocumentAsync(host);
        var paths = document["paths"]!.AsObject();

        var batch = paths["/OxQL/batch"]!["post"]!;

        batch["summary"]!.GetValue<string>().Should().Be(OxQLDocumentation.BatchSummary);
        Reference(batch["requestBody"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLBatchBody));
        Reference(batch["responses"]!["200"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLBatchAnswer));
        batch["responses"]!.AsObject().Select(response => response.Key).Should().BeEquivalentTo(["200", "400", "401", "413", "500"]);

        var explain = paths["/OxQL/explain"]!["post"]!;

        explain["summary"]!.GetValue<string>().Should().Be(OxQLDocumentation.ExplainSummary);
        Reference(explain["requestBody"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLExplainBody));
        Reference(explain["responses"]!["200"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLExplainAnswer));
        explain["responses"]!.AsObject().Select(response => response.Key).Should().BeEquivalentTo(["200", "304", "400", "401", "403", "404", "413", "429", "500"]);
        explain["parameters"]!.AsArray().Select(parameter => parameter!["name"]!.GetValue<string>()).Should().Equal("X-OxQL-Contract");

        var health = paths["/OxQL/health"]!["get"]!;

        health["summary"]!.GetValue<string>().Should().Be(OxQLDocumentation.HealthSummary);
        health["parameters"]!.AsArray().Select(parameter => (parameter!["name"]!.GetValue<string>(), parameter["in"]!.GetValue<string>()))
            .Should().BeEquivalentTo([("shallow", "query"), ("X-OxQL-Contract", "header")]);
        health["parameters"]!.AsArray().Single(parameter => parameter!["name"]!.GetValue<string>() == "shallow")!["description"]!.GetValue<string>().Should().Contain("remote");
        Reference(health["responses"]!["200"]!["content"]!["application/json"]!["schema"]).Should().Be(nameof(OxQLHealthAnswer));
        health["responses"]!.AsObject().Select(response => response.Key).Should().Equal("200");
    }

    [Fact]
    public async Task Describing_the_routes_changes_nothing_about_how_a_request_is_read()
    {
        using var host = new SampleHost();

        // The header is read where it always was: an unknown value is refused, an empty one is contract 1, a padded 2 is contract 2.
        var unknown = await host.Client(contract: 3).PostAsync("/OxQL/query", SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [] }"""));

        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await SampleHost.Body(unknown))!["errors"]![0]!["params"]!["reason"]!.GetValue<string>().Should().Be("contract");

        async Task<JsonNode?> With(string? header)
        {
            var client = host.Client(contract: null);

            if (header is not null)
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-OxQL-Contract", header);

            var response = await client.PostAsync("/OxQL/query", SampleHost.Json("""{ "entityType": "probe.order", "pipeline": [{ "page": { "limit": 1 } }] }"""));

            response.StatusCode.Should().Be(HttpStatusCode.OK, header);

            return await SampleHost.Body(response);
        }

        (await With(null))!["pageInfo"]!.AsObject().ContainsKey("count").Should().BeFalse("no header is contract 1");
        (await With(""))!["pageInfo"]!.AsObject().ContainsKey("count").Should().BeFalse("an empty header is contract 1");
        (await With("1"))!["pageInfo"]!.AsObject().ContainsKey("count").Should().BeFalse();
        (await With(" 2 "))!["pageInfo"]!.AsObject().ContainsKey("count").Should().BeTrue();
        (await With("2"))!["pageInfo"]!.AsObject().ContainsKey("count").Should().BeTrue();

        // A JSON media type with a suffix is read as before: nothing constrains the content type beyond the host's formatters.
        var suffixed = await host.Client().PostAsync("/OxQL/query", new StringContent("""{ "entityType": "probe.order", "pipeline": [] }""", System.Text.Encoding.UTF8, "application/vnd.oxql+json"));

        suffixed.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
