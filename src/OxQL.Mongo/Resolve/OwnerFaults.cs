using System.Text.Json.Nodes;
using OxQL.Core.Binding;

namespace OxQL.Mongo.Resolve;

/// <summary>
/// What of an owner's answer reaches this host's caller. An owner's own fault
/// (<c>INTERNAL_ERROR</c>, <c>internal_error</c>) may carry its exception text when that owner runs
/// with <c>IncludeErrorDetails</c>; the caller of this host gets a fixed text instead, whatever the
/// owner's setting, and the detail stays in the owner's log.
/// </summary>
internal static class OwnerFaults
{
    /// <summary>The text an owner's fault is reported with.</summary>
    public const string Message = "The owner failed while answering; the detail is in the owner's log.";

    /// <summary>Replaces, in place, the title of an owner's fault envelope and the message of each <c>INTERNAL_ERROR</c> it lists.</summary>
    public static JsonNode? Scrubbed(JsonNode? answer)
    {
        if (answer is not JsonObject body)
            return answer;

        if (body["type"] is JsonValue type && type.TryGetValue<string>(out var kind) && kind == "internal_error")
            body["title"] = Message;

        if (body["errors"] is JsonArray errors)
            foreach (var error in errors.OfType<JsonObject>())
                if (error["code"] is JsonValue code && code.TryGetValue<string>(out var text) && text == Codes.InternalError)
                    error["message"] = Message;

        return answer;
    }
}
