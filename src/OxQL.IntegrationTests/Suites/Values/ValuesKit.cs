using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using OxQL.IntegrationTests.Harness;

namespace OxQL.IntegrationTests.Suites.Values;

/// <summary>What the values suites share: the request that ends in <c>values</c>, the list of its answer, and the group a caller would write by hand for the same list.</summary>
internal static class ValuesKit
{
    public static JsonObject Request(string entity, string values, string before = "", string? filter = null, string extra = "") =>
        JsonNode.Parse($$"""{ "entityType": "{{entity}}", {{extra}} {{(filter is null ? "" : $"\"filter\": {filter},")}} "pipeline": [ {{before}}{{(before.Length > 0 ? "," : "")}} { "values": { {{values}} } } ] }""")!.AsObject();

    public static async Task<WireAnswer> Ask(string entity, string values, string before = "", string? filter = null, string extra = "") =>
        await (await Lab.ClientForAsync(entity)).QueryAsync(Request(entity, values, before, filter, extra));

    /// <summary>JSON text with every character written as itself, so a value reads as the corpus spells it.</summary>
    public static readonly JsonSerializerOptions Plain = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The list as it stands in the answer: each value as its JSON text, with its count when the row carries one.</summary>
    public static List<(string Value, int? Count)> Listed(WireAnswer answer) =>
        answer.ShouldBeOk().Items.Select(item => (item!["value"]?.ToJsonString(Plain) ?? "null", item["count"]?.GetValue<int>())).ToList();

    /// <summary>The group a caller writes by hand for the same list: the path as <c>value</c>, the rows as <c>count</c>, most first then by value.</summary>
    public static async Task<WireAnswer> ByHand(string entity, string path, string before = "", bool counts = true, string? filter = null) =>
        await (await Lab.ClientForAsync(entity)).QueryAsync(JsonNode.Parse($$"""
            { "entityType": "{{entity}}", {{(filter is null ? "" : $"\"filter\": {filter},")}}
              "pipeline": [ {{before}}{{(before.Length > 0 ? "," : "")}}
                { "group": { "by": [{ "path": "{{path}}", "as": "value" }], "fields": { "count": { "count": true } } } },
                { "sort": [ {{(counts ? """{ "count": "desc" },""" : "")}} { "value": "asc" } ] },
                {{(counts ? "" : """{ "project": { "value": 1 } },""")}}
                { "page": { "limit": 500 } } ] }
            """)!.AsObject());

    public static string Texts(WireAnswer answer) => answer.ShouldBeOk().Items.ToJsonString();
}
