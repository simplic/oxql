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

    /// <summary>
    /// Every language feature of OxQL 2.1 and the explain envelope (DESIGN §3.11, §4): a caller gates
    /// on it before sending a 2.1 construct or reading a 2.1 explain answer.
    /// </summary>
    public const string Oxql21 = "oxql.2.1";

    /// <summary>Chains across services by remote continuation (DESIGN §3.5.3); only with a remote query client.</summary>
    public const string ResolveChain = "resolve.chain";

    /// <summary><c>POST /oxql/explain</c> answers.</summary>
    public const string Explain = "explain";

    /// <summary>The engine version, as health and explain publish it.</summary>
    public static string? Version { get; } = typeof(EngineCapabilities).Assembly.GetName().Version?.ToString();

    /// <summary>The capabilities of a host with the given features and options.</summary>
    public static IReadOnlyList<string> Of(bool remoteResolve, bool compat, bool explain)
    {
        var capabilities = new List<string> { "batch", "group.page", "page.offset", "any", Oxql21 };

        if (remoteResolve)
        {
            capabilities.Add("resolve.remote");
            capabilities.Add("semiJoin");
            capabilities.Add(ResolveChain);
        }

        if (explain)
            capabilities.Add(Explain);

        if (compat)
            capabilities.Add("compat.v1");

        return capabilities;
    }
}
