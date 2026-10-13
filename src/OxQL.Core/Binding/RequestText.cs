using System.Collections;
using System.Reflection;
using System.Text.Json;
using OxQL.Core.Models;

namespace OxQL.Core.Binding;

/// <summary>
/// A request that holds a member name or a text that is no Unicode text: half of a surrogate pair
/// written as an escape, or bytes that are no UTF-8. Such a body is well-formed JSON, so it parses
/// and its parts stay as written until something reads one as a string; the read then fails. A
/// host answers it from the body before it is bound (<c>INVALID_REQUEST_BODY</c> with the place);
/// the binder answers the same code for a request that reaches it without that, so the caller's
/// mistake is never the engine's fault.
/// </summary>
internal static class RequestText
{
    /// <summary>The <c>reason</c> of the refusal, as a host says it for a body.</summary>
    public const string ReasonEncoding = "encoding";

    private const int MaxModelDepth = 64;

    /// <summary>The refusal of a request that holds such a text, said without a place: the binder has the request, not its body.</summary>
    public static QueryValidationError Refused() => new()
    {
        Code = Codes.InvalidRequestBody,
        Message = "A member name or a text of the request is no Unicode text: it holds half of a surrogate pair, or bytes that are no UTF-8.",
        Params = new Dictionary<string, object?> { ["reason"] = ReasonEncoding },
    };

    /// <summary>
    /// Whether <paramref name="request"/> holds a name or a text that is no Unicode text in any of
    /// the JSON values it carries as written. Asked only once a read has failed, to tell the
    /// caller's mistake from a fault.
    /// </summary>
    public static bool HoldsBroken(QueryRequest request) => Holds(request, 0);

    private static bool Holds(object? value, int depth)
    {
        switch (value)
        {
            case null or string:
                return false;

            case JsonElement element:
                return IsBroken(element);

            case IDictionary map:
                foreach (var entry in map.Values)
                    if (Holds(entry, depth + 1))
                        return true;

                return false;

            case IEnumerable sequence:
                foreach (var item in sequence)
                    if (Holds(item, depth + 1))
                        return true;

                return false;
        }

        var type = value.GetType();

        // Only the request's own model is walked; a value of any other type holds no JSON as written.
        if (depth >= MaxModelDepth || type.Assembly != typeof(QueryRequest).Assembly || type.IsEnum)
            return false;

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanRead || property.GetIndexParameters().Length > 0)
                continue;

            object? held;

            try
            {
                held = property.GetValue(value);
            }
            catch (TargetInvocationException)
            {
                // A member computed from a JSON value fails as the binder's read did; the value itself is walked as a member of its own.
                continue;
            }

            if (Holds(held, depth + 1))
                return true;
        }

        return false;
    }

    /// <summary>Whether a JSON value holds a name or a text that is no Unicode text; it visits each node once.</summary>
    public static bool IsBroken(JsonElement root)
    {
        var pending = new Stack<JsonElement>();

        pending.Push(root);

        while (pending.TryPop(out var current))
        {
            switch (current.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var member in current.EnumerateObject())
                    {
                        if (!IsText(member))
                            return true;

                        pending.Push(member.Value);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in current.EnumerateArray())
                        pending.Push(item);

                    break;

                case JsonValueKind.String when !IsText(current):
                    return true;
            }
        }

        return false;
    }

    private static bool IsText(JsonProperty member)
    {
        try
        {
            _ = member.Name;

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsText(JsonElement text)
    {
        try
        {
            _ = text.GetString();

            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
