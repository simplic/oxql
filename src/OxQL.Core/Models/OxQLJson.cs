using System.Text.Json;
using System.Text.Json.Serialization;

namespace OxQL.Core.Models;

/// <summary>
/// The serializer options of the wire: what the controller answers with, what a batch entry is
/// rendered with, and what a host's remote query client sends a batch to an owner and reads its
/// answer with. One instance, so both ends of a remote call spell a request the same way.
/// </summary>
public static class OxQLJson
{
    /// <summary>
    /// How deep an answer may nest. The default 64 (MVC's 32) is less than what the wire carries: a
    /// stored document nests up to the database's 100 levels, and explain's emitted stages of a
    /// <c>flatten</c> unwind or a chain of joins wrap expressions in expressions. Requests stay bounded
    /// by their size limit.
    /// </summary>
    public const int MaxDepth = 256;

    /// <summary>camelCase member names, null members omitted.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = MaxDepth,
    };
}
