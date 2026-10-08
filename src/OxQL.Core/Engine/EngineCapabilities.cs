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

    /// <summary><c>POST /oxql/explain</c> answers.</summary>
    public const string Explain = "explain";

    /// <summary>The engine version, as health and explain publish it.</summary>
    public static string? Version { get; } = typeof(EngineCapabilities).Assembly.GetName().Version?.ToString();

    /// <summary>
    /// What every engine listed before contract 2 became the marker: batches, paging after a group,
    /// paging by offset, <c>any</c>, and an unwind that takes its collection out of the row. A host
    /// that answers contract 1 still does all five for such a caller, and a contract 1 client may gate
    /// on a name, so the list a contract 1 caller reads keeps them (<see cref="Of"/>).
    /// </summary>
    public static IReadOnlyList<string> Contract1 { get; } = ["batch", "group.page", "page.offset", "any", "unwind.keepPath"];

    /// <summary>
    /// The capabilities of a host with the given features and options: only what depends on the host.
    /// What every contract 2 engine has (batches, paging after a group, <c>any</c>) is not listed; the
    /// contract number says it (DG2 M12). For a contract 1 caller (<paramref name="contract1"/>: the
    /// request carries no contract header while the host answers contract 1) the list begins with
    /// <see cref="Contract1"/>, as it did when that caller was written: of those, <c>page.offset</c>
    /// and <c>unwind.keepPath</c> are contract 1 syntax only.
    /// </summary>
    public static IReadOnlyList<string> Of(bool remoteResolve, bool compat, bool explain, bool contract1 = false)
    {
        var capabilities = new List<string>();

        if (contract1 && compat)
            capabilities.AddRange(Contract1);

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
