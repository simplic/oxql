using System.Text.Json.Nodes;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// What a contract 2 refusal carries in <c>params</c>, by code, as the operations guide lists it
/// (<i>Codes</i>, <i>What a limit refusal carries</i>). Every answer a suite receives through
/// <see cref="LabClient"/> is held against it, so each place of the engine that refuses with one of
/// these codes is checked by whatever case provokes it, and a new place cannot leave them out.
/// </summary>
public static class RefusalParams
{
    private static readonly string[] Limit = ["limit", "max"];

    /// <summary>The members <c>params</c> holds for a code, on every refusal with it.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>
    {
        ["OPTION_NOT_APPLICABLE"] = ["option", "reason"],
        ["UNKNOWN_STAGE"] = ["reason"],
        ["UNKNOWN_STAGE_MEMBER"] = ["stage", "reason"],
        ["UNKNOWN_REQUEST_MEMBER"] = ["reason"],
        ["INVALID_REQUEST_BODY"] = ["reason"],
        ["INVALID_FILTER"] = ["reason"],
        ["FILTER_NOT_PLACEABLE"] = ["paths", "births"],
        ["CURSOR_INVALID"] = ["reason"],
        ["UNKNOWN_ENUM_MEMBER"] = ["values"],
        ["UNKNOWN_VARIANT"] = ["variant", "variants"],
        ["QUERY_TIMEOUT"] = ["maxTimeMs"],
        ["MAX_PIPELINE_STAGES_EXCEEDED"] = Limit, ["MAX_LOOKUP_STAGES_EXCEEDED"] = Limit, ["MAX_UNWIND_STAGES_EXCEEDED"] = Limit, ["MAX_RESOLVE_STAGES_EXCEEDED"] = Limit,
        ["MAX_CONTINUED_STAGES_EXCEEDED"] = Limit, ["MAX_GROUP_FIELDS_EXCEEDED"] = Limit, ["MAX_PROJECTION_FIELDS_EXCEEDED"] = Limit, ["MAX_CONDITIONS_EXCEEDED"] = Limit,
        ["MAX_FILTER_ENTRIES_EXCEEDED"] = Limit, ["MAX_VARIABLES_EXCEEDED"] = Limit, ["PAGE_SIZE_EXCEEDED"] = Limit, ["LOOKUP_LIMIT_EXCEEDED"] = Limit, ["VALUES_LIMIT_EXCEEDED"] = Limit,
        ["REGEX_TOO_LONG"] = Limit, ["BATCH_TOO_LARGE"] = Limit, ["REQUEST_TOO_LARGE"] = Limit,
    };

    /// <summary>Fails the case whose answer carries a refusal without the members its code has.</summary>
    public static void Hold(WireAnswer answer)
    {
        if (answer.Body is not JsonObject body)
            return;

        Check(body["errors"] as JsonArray, answer);

        // The entries of a batch, each a refusal or a page of its own.
        foreach (var entry in body["results"] as JsonArray ?? [])
            Check(entry?["errors"] as JsonArray, answer);
    }

    private static void Check(JsonArray? errors, WireAnswer answer)
    {
        foreach (var error in errors?.OfType<JsonObject>() ?? [])
        {
            // An owner's error is the owner's to shape; it travels with where it came from.
            if (error["code"]?.GetValue<string>() is not { } code || !Required.TryGetValue(code, out var members) || error["params"]?["owner"] is not null)
                continue;

            var missing = members.Where(member => error["params"] is not JsonObject carried || !carried.ContainsKey(member)).ToList();

            if (missing.Count > 0)
                throw new Xunit.Sdk.XunitException($"A {code} refusal carries no params.{string.Join(", params.", missing)}: {error.ToJsonString()}\n    request: {answer.Request}");
        }
    }
}
