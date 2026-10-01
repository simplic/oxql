using Xunit;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// The tests that measure time: they run one after another, after every other test, so what they
/// measure is the engine and not the tests beside them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimedCollection
{
    public const string Name = "Timed";
}
