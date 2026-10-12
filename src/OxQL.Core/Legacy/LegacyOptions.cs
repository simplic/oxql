// LEGACY(compat.v1): contract 1 legacy support. Remove with contract 1 compatibility; see src/docs/LEGACY.md.
namespace OxQL.Core.Legacy;

/// <summary>
/// The legacy pipeline of contract 1 compatibility (<c>OxQL:Compat:Legacy</c>): what the engine
/// before contract 2 (packages 1.1) answered and the contract 2 engine has no form for. It exists
/// for the time callers are migrated in and is removed with compatibility itself
/// (<c>src/docs/LEGACY.md</c>).
/// </summary>
public sealed class LegacyOptions
{
    /// <summary>
    /// The one switch. On: a contract 1 request (one without the contract header) is answered by the
    /// legacy pipeline as the 1.1 engine answered it; which requests, <see cref="Always"/> says. Off:
    /// every contract 1 request is answered by the compatibility rewrite and the contract 2 engine,
    /// and what the rewrite cannot express is refused. Without <c>Compat:Enabled</c> there is no
    /// contract 1 request and this is never read.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether every contract 1 request runs on the legacy pipeline. On by default: a caller without
    /// the contract header gets the answers it got from the 1.1 query path: the rows with every
    /// stored member, the envelope, the cursors, the count, the refusals. Off: the compatibility
    /// rewrite answers first, the contract 2 engine in contract 1 spelling, and the legacy pipeline
    /// only what the rewrite cannot express (a v1 <c>lookup</c> or <c>resolve</c>, a path the model
    /// does not declare, a stage after the page). That path is the published 2.0 behaviour of
    /// contract 1.
    /// </summary>
    public bool Always { get; set; } = true;

    /// <summary>
    /// The stored member that holds a document's organisation, in the queried collection and in
    /// every collection a v1 <c>lookup</c> reads. Every collection is filtered by this one name; a
    /// document without it is never read.
    /// </summary>
    public string OrganisationPath { get; set; } = "OrganizationId";

    /// <summary>
    /// The collections a v1 <c>lookup</c> may read, by name, ignoring case. Empty: a collection of
    /// the queried entity's database, read for the caller's organisation only. Name the collections
    /// here to narrow that.
    /// </summary>
    public HashSet<string> AllowedLookupSources { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The default of <see cref="MaxTimeMs"/>: ten minutes.</summary>
    public const int DefaultMaxTimeMs = 600_000;

    /// <summary>
    /// The time a legacy request may take in the database, in milliseconds, for its page and its
    /// count together. The default is ten minutes and not the contract 2 engine's
    /// <c>Execution:MaxTimeMs</c>, so a long-running report still answers. It is not clamped; 0 or
    /// less takes the default. A request its caller abandons is cancelled whatever this says.
    /// </summary>
    public int MaxTimeMs { get; set; } = DefaultMaxTimeMs;

    /// <summary>The effective ceiling: <see cref="MaxTimeMs"/>, or the default where it is 0 or less.</summary>
    public int EffectiveMaxTimeMs => MaxTimeMs > 0 ? MaxTimeMs : DefaultMaxTimeMs;
}
