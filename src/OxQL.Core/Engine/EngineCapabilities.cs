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
    /// <summary>
    /// The contract this engine speaks. Contract 2 is the marker of this package: every engine that
    /// reports it has the whole contract 2 language and the explain answer (DESIGN §3.11, §4), so a
    /// caller gates on <c>engine.contract</c>, never on a capability per feature.
    /// </summary>
    public const int Contract = 2;

    /// <summary>Chains across services by remote continuation (DESIGN §3.5.3); only with a remote query client.</summary>
    public const string ResolveChain = "resolve.chain";

    /// <summary>A <c>lookup</c> whose <c>from</c> is another service's entity (DESIGN §3.4.4); only with a remote query client.</summary>
    public const string LookupRemote = "lookup.remote";

    /// <summary><c>unwind.keepPath</c>: an unwind can take its collection out of the row (<c>keepPath: false</c>).</summary>
    public const string UnwindKeepPath = "unwind.keepPath";

    /// <summary><c>POST /oxql/explain</c> answers.</summary>
    public const string Explain = "explain";

    /// <summary>The engine version, as health and explain publish it.</summary>
    public static string? Version { get; } = typeof(EngineCapabilities).Assembly.GetName().Version?.ToString();

    /// <summary>The capabilities of a host with the given features and options.</summary>
    public static IReadOnlyList<string> Of(bool remoteResolve, bool compat, bool explain)
    {
        var capabilities = new List<string> { "batch", "group.page", "page.offset", "any", UnwindKeepPath };

        if (remoteResolve)
        {
            capabilities.Add("resolve.remote");
            capabilities.Add("semiJoin");
            capabilities.Add(ResolveChain);
            capabilities.Add(LookupRemote);
        }

        if (explain)
            capabilities.Add(Explain);

        if (compat)
            capabilities.Add("compat.v1");

        return capabilities;
    }
}
