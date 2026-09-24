namespace OxQL.Core.Engine;

/// <summary>What an engine can do beyond the query path; the host publishes it on <c>/health</c>.</summary>
public interface IEngineFeatures
{
    /// <summary>Whether remote resolves and semi-joins can run: a remote query client is installed.</summary>
    bool RemoteResolve { get; }
}

/// <summary>The capability names, in the fixed spelling of the design, and the contract number.</summary>
public static class EngineCapabilities
{
    /// <summary>The contract this engine speaks.</summary>
    public const int Contract = 2;

    /// <summary>The capabilities of a host with the given features and options.</summary>
    public static IReadOnlyList<string> Of(bool remoteResolve, bool compat, bool explain)
    {
        var capabilities = new List<string> { "batch", "group.page", "page.offset", "any" };

        if (remoteResolve)
        {
            capabilities.Add("resolve.remote");
            capabilities.Add("semiJoin");
        }

        if (explain)
            capabilities.Add("explain");

        if (compat)
            capabilities.Add("compat.v1");

        return capabilities;
    }
}
