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
    /// <summary>camelCase member names, null members omitted.</summary>
    public static readonly JsonSerializerOptions Wire = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
