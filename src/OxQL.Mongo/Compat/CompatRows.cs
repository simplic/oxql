using System.Text.Json;
using System.Text.Json.Nodes;
using MongoDB.Bson;

namespace OxQL.Mongo.Compat;

/// <summary>
/// The contract 1 output pass: the row as the driver returned it, rendered by the v1
/// converter, so <c>_id</c> and the storage names come back exactly as they did.
/// </summary>
public static class CompatRows
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new BsonDocumentJsonConverter() } };

    /// <summary>Renders one row.</summary>
    public static JsonNode? Encode(BsonDocument row) => JsonSerializer.SerializeToNode(row, Options);
}
