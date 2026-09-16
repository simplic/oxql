using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model;
using OxQL.Model.Addon;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;

namespace OxQL.Tests.Bind;

/// <summary>What every binding test needs: models, a signing key, an organisation, and the request helpers.</summary>
internal static class BindHost
{
    public const string SigningKey = "test-signing-key";

    public static readonly Guid Organisation = Guid.Parse("a8d899a4-2029-4806-a4b7-a414eab21801");

    public static readonly CursorCodec Cursors = new(SigningKey);

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly Dictionary<string, EntityModel> vendored = new(StringComparer.Ordinal);

    /// <summary>The CLR fixture graph: every kind, every registry fact.</summary>
    public static EntityModel Probe => ProbeModel.Clr;

    /// <summary>A vendored service document as a model.</summary>
    public static EntityModel Vendored(string name)
    {
        lock (vendored)
        {
            if (!vendored.TryGetValue(name, out var model))
                vendored[name] = model = DocumentModelBuilder.Build(ProbeModel.ReadFixture(name));

            return model;
        }
    }

    public static OxQLOptions Options(Action<OxQLOptions>? configure = null)
    {
        var options = new OxQLOptions { Cursor = { SigningKey = SigningKey } };

        configure?.Invoke(options);

        return options;
    }

    public static RequestContext Context(OxQLOptions? options = null, Guid? organisation = null, IAddonDefinitionSource? addons = null, int contract = 2) => new()
    {
        Organisation = organisation ?? Organisation,
        Options = options ?? Options(),
        AddonSource = addons ?? EmptyAddonDefinitionSource.Instance,
        Contract = contract,
    };

    public static QueryRequest Parse(string json) =>
        JsonSerializer.Deserialize<QueryRequest>(json, Json) ?? throw new InvalidOperationException("The request did not parse.");

    /// <summary>A request over one entity with the given pipeline JSON.</summary>
    public static QueryRequest Request(string entity, string pipelineJson, string? variablesJson = null) =>
        Parse($$"""{ "entityType": "{{entity}}", {{(variablesJson is null ? "" : $"\"variables\": {variablesJson},")}} "pipeline": {{pipelineJson}} }""");

    public static async Task<BindOutcome> BindAsync(EntityModel model, QueryRequest request, RequestContext? context = null) =>
        await new Binder(model, Cursors).BindAsync(request, context ?? Context(), CancellationToken.None);

    /// <summary>Binds and asserts success.</summary>
    public static async Task<BoundPipeline> BoundAsync(EntityModel model, string entity, string pipelineJson, RequestContext? context = null, string? variablesJson = null)
    {
        var outcome = await BindAsync(model, Request(entity, pipelineJson, variablesJson), context);

        outcome.Should().BeOfType<BindOutcome.Bound>($"the request should bind; got: {Describe(outcome)}");

        return ((BindOutcome.Bound)outcome).Pipeline;
    }

    /// <summary>Binds and asserts a refusal, returning it.</summary>
    public static async Task<Refusal> RefusedAsync(EntityModel model, string entity, string pipelineJson, RequestContext? context = null, string? variablesJson = null)
    {
        var outcome = await BindAsync(model, Request(entity, pipelineJson, variablesJson), context);

        outcome.Should().BeOfType<BindOutcome.Failed>("the request should be refused");

        return ((BindOutcome.Failed)outcome).Refusal;
    }

    /// <summary>Binds and asserts a refusal carrying <paramref name="code"/>.</summary>
    public static async Task<QueryValidationError> ErrorAsync(EntityModel model, string entity, string pipelineJson, string code, RequestContext? context = null, string? variablesJson = null)
    {
        var refusal = await RefusedAsync(model, entity, pipelineJson, context, variablesJson);
        var error = refusal.Errors?.FirstOrDefault(candidate => candidate.Code == code);

        error.Should().NotBeNull($"expected {code}; got: {Describe(refusal)}");

        return error!;
    }

    public static string Describe(BindOutcome outcome) => outcome switch
    {
        BindOutcome.Failed failed => Describe(failed.Refusal),
        BindOutcome.Bound bound => "bound: " + bound.Pipeline.Canonical,
        _ => outcome.ToString() ?? "",
    };

    public static string Describe(Refusal refusal) =>
        $"{refusal.Status} {refusal.Type}: " + string.Join("; ", (refusal.Errors ?? []).Select(error => $"{error.Code}@{error.Stage} {error.Path}: {error.Message}"));

    /// <summary>A valid operand for a kind, as the schema's wire encoding returns it.</summary>
    public static string ValidOperand(Kind kind, ShapeDef? leaf) => kind switch
    {
        Kind.String => "\"abc\"",
        Kind.Int or Kind.Long => "5",
        Kind.Double => "1.5",
        Kind.Decimal => "\"12.50\"",
        Kind.Bool => "true",
        Kind.Guid => "\"195fb742-82b3-405e-b77b-42838eb0aaa9\"",
        Kind.Date => "\"2024-01-02\"",
        Kind.DateTime => "\"2024-01-02T03:04:05Z\"",
        Kind.TimeSpan => "\"PT1H30M\"",
        Kind.Enum => "\"" + (leaf?.Type?.EnumValues.FirstOrDefault()?.Name ?? "Missing") + "\"",
        Kind.Binary => "\"AQID\"",
        _ => "null",
    };

    /// <summary>An operand of the wrong JSON kind, and the code it should be refused with.</summary>
    public static (string Operand, string Code) InvalidOperand(Kind kind) => kind switch
    {
        Kind.String => ("5", Codes.InvalidOperand),
        Kind.Int or Kind.Long => ("\"abc\"", Codes.InvalidOperand),
        Kind.Double => ("\"abc\"", Codes.InvalidOperand),
        Kind.Decimal => ("\"twelve\"", Codes.InvalidOperand),
        Kind.Bool => ("\"yes\"", Codes.InvalidOperand),
        Kind.Guid => ("\"not-a-guid\"", Codes.InvalidOperand),
        Kind.Date => ("\"2024-01-02T03:04:05Z\"", Codes.InvalidOperand),
        Kind.DateTime => ("\"2024-01-02\"", Codes.InvalidOperand),
        Kind.TimeSpan => ("\"01:30:00\"", Codes.InvalidOperand),
        Kind.Enum => ("\"NoSuchMember\"", Codes.UnknownEnumMember),
        Kind.Binary => ("\"not base64!\"", Codes.InvalidOperand),
        _ => ("{}", Codes.InvalidOperand),
    };

    public static readonly string[] Operators = ["eq", "neq", "gt", "gte", "lt", "lte", "in", "nin", "contains", "startsWith", "endsWith", "exists", "regex"];
}
