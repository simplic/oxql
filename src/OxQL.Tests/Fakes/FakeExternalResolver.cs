using OxQL.Core.Interfaces;

namespace OxQL.Tests.Fakes;

/// <summary>
/// A test fake implementation of IExternalResolver for unit testing.
/// </summary>
public sealed class FakeExternalResolver : IExternalResolver
{
    private readonly Dictionary<string, object?> _data = new();

    public string Source { get; }

    public FakeExternalResolver(string source)
    {
        Source = source;
    }

    /// <summary>
    /// Adds a key-value pair to the fake resolver data.
    /// </summary>
    public FakeExternalResolver WithData(string key, object? value)
    {
        _data[key] = value;
        return this;
    }

    public Task<IReadOnlyDictionary<string, object?>> ResolveAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<string, object?>();
        foreach (var key in keys)
        {
            if (_data.TryGetValue(key, out var value))
                results[key] = value;
        }
        return Task.FromResult<IReadOnlyDictionary<string, object?>>(results);
    }
}
