using System.Diagnostics;
using FluentAssertions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using OxQL.Model.Build;
using OxQL.Tests.Model.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace OxQL.Tests.Model;

/// <summary>
/// Why the model is built in the startup filter and never during service registration: the
/// walk freezes every type it looks up. And the walk is cheap enough to run there.
/// </summary>
public class FreezeAndTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void After_the_walk_a_registration_for_a_walked_type_throws_and_an_unwalked_type_still_accepts_one()
    {
        var declarations = new[] { new EntityDeclaration("probe.frozen", "probe.frozen", typeof(FrozenModel), "frozen", null, false) };
        var model = ClrModelBuilder.Build(declarations);

        model.Entities["probe.frozen"].Path("part.a")!.Storage.Should().Be("Part.A");

        // A class map for a walked type: the registry already holds one.
        var lateMap = new BsonClassMap(typeof(FrozenPart));
        lateMap.AutoMap();
        lateMap.GetMemberMap("A")!.SetElementName("renamed_by_late_map");

        ((Action)(() => BsonClassMap.RegisterClassMap(lateMap))).Should().Throw<ArgumentException>();
        BsonClassMap.TryRegisterClassMap<FrozenPart>().Should().BeFalse();

        // A serializer for a walked type: the answer is frozen.
        ((Action)(() => BsonSerializer.RegisterSerializer(typeof(FrozenPart), new OpaqueSerializer<FrozenPart>())))
            .Should().Throw<BsonSerializationException>();

        var again = ClrModelBuilder.Build(declarations);

        again.Entities["probe.frozen"].Path("part.a")!.Storage.Should().Be("Part.A", "the late registrations changed nothing");

        // A type never looked up still accepts a registration, and it takes effect.
        BsonSerializer.RegisterSerializer(typeof(NeverWalked), new OpaqueSerializer<NeverWalked>());
        BsonSerializer.LookupSerializer(typeof(NeverWalked)).Should().BeOfType<OpaqueSerializer<NeverWalked>>();
    }

    [Fact]
    public void The_walk_costs_tens_of_milliseconds()
    {
        // Warm the driver on a type nobody measures so the JIT cost of the first lookup is not in the numbers.
        _ = BsonSerializer.LookupSerializer(typeof(WarmUp));

        var cold = Stopwatch.StartNew();
        var model = ClrModelBuilder.Build([typeof(OrderModel).Assembly], ProbeModel.RetiredIds);
        cold.Stop();

        var warm = Stopwatch.StartNew();
        ClrModelBuilder.Build([typeof(OrderModel).Assembly], ProbeModel.RetiredIds);
        warm.Stop();

        output.WriteLine($"clr fixture assembly: {model.Entities.Values.Sum(entity => entity.Paths.Count)} paths, first build {cold.Elapsed.TotalMilliseconds:F1} ms, second {warm.Elapsed.TotalMilliseconds:F1} ms");

        foreach (var name in ProbeModel.VendoredDocuments)
        {
            var json = ProbeModel.ReadFixture(name);
            var timer = Stopwatch.StartNew();
            var document = DocumentModelBuilder.Build(json);
            timer.Stop();

            output.WriteLine($"{name}: {document.Entities.Values.Sum(entity => entity.Paths.Count)} paths in {timer.Elapsed.TotalMilliseconds:F1} ms");
            timer.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1), name);
        }

        cold.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "a cold JIT build of the fixture assembly");
        warm.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500));
    }

    public class NeverWalked
    {
        public string? A { get; set; }
    }

    public class WarmUp
    {
        public Guid Id { get; set; }
        public string? A { get; set; }
        public List<WarmUp> Children { get; set; } = [];
    }

    private sealed class OpaqueSerializer<T> : SerializerBase<T>
    {
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, T value) => context.Writer.WriteString("");

        public override T Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            context.Reader.ReadString();
            return default!;
        }
    }
}
