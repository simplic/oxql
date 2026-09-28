using System.Runtime.CompilerServices;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace OxQL.Tests.Model;

/// <summary>
/// Replicates the one registration every host makes before the model is built: the base
/// package's <c>GuidSerializer(GuidRepresentation.Standard)</c>. It runs once per test process,
/// before any type is looked up, because a registration after the first lookup throws. Then the
/// class maps the polymorphism and reference fixtures register as a host would.
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

        // The fixtures' class maps, before any test runs: a registration made while another test
        // class's model build is inside its tracked lookup is counted as that build's own and is
        // never a variant, so registering lazily from parallel test classes is flaky.
        Fixtures.Polymorphism.PolymorphismRegistrations.Ensure();
        Fixtures.References.ReferenceModel.Register();
        Fixtures.Variants.VariantRegistrations.Ensure();
        Bind.Fixtures.Resolve.ResolveModel.Register();
    }
}
