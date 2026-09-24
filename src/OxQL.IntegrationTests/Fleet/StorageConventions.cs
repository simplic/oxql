using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// The storage conventions a real service registers before anything touches the driver: Guids
/// as binary subtype 4 (<see cref="GuidRepresentation.Standard"/>). Registered once per
/// process, when the assembly loads, so no serializer lookup (the model build included) can
/// run before it and freeze the driver's default.
/// </summary>
internal static class StorageConventions
{
    [ModuleInitializer]
    internal static void Register() =>
        BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
}
