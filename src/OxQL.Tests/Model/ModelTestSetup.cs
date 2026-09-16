using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace OxQL.Tests.Model;

/// <summary>
/// Replicates the one registration every host makes before the model is built: the base
/// package's <c>GuidSerializer(GuidRepresentation.Standard)</c>. It runs once per test process,
/// before any type is looked up, because a registration after the first lookup throws.
/// </summary>
internal static class ModelTestSetup
{
    [ModuleInitializer]
    internal static void RegisterHostSerializers()
    {
        try
        {
            BsonSerializer.RegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
        }
        catch (BsonSerializationException)
        {
            // Already registered by an earlier initializer in the same process.
        }
    }
}
