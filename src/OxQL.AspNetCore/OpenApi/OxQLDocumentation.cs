namespace OxQL.AspNetCore.OpenApi;

/// <summary>
/// Where the OxQL routes point a reader of their API description: the language is documented in
/// one place, and a description of a route names that place instead of repeating it. The summaries
/// and descriptions the routes carry are here too, so the address stands once.
/// </summary>
public static class OxQLDocumentation
{
    /// <summary>The address of the OxQL documentation.</summary>
    public const string Url = "https://github.com/simplic/oxql/blob/master/src/docs/";

    /// <summary>The request language: stages, operators, paging, the answer and its refusals.</summary>
    public const string QuerySyntax = Url + "oxql-query-syntax.md";

    /// <summary>What a request means where it is not obvious: null, collections, joins, counts, grouping.</summary>
    public const string Semantics = Url + "oxql-semantics.md";

    /// <summary>The routes as a host runs them: statuses, codes, limits, health, explain.</summary>
    public const string Operations = Url + "oxql-operations.md";

    /// <summary>Contract 1: what a request without the contract header is answered by.</summary>
    public const string Legacy = Url + "LEGACY.md";

    /// <summary>The route that publishes the limits and capabilities of a host.</summary>
    public const string HealthRoute = "GET /OxQL/health";

    private const string Contract = " Send the header `" + OxQLQueryService.ContractHeader + ": 2`; a request without it is read as contract 1 (legacy, see " + Legacy + ").";

    private const string Pointers = " The language is described in the OxQL documentation (" + QuerySyntax + "); the limits of this host are published by `" + HealthRoute + "`.";

    /// <summary>The summary of the query route.</summary>
    public const string QuerySummary = "Runs one OxQL query and answers a page of rows.";

    /// <summary>The description of the query route.</summary>
    public const string QueryDescription =
        "Reads one entity of this service through a pipeline of stages and answers a page of rows with what is known of their count, or a refusal with a code." + Pointers + Contract;

    /// <summary>The summary of the batch route.</summary>
    public const string BatchSummary = "Runs several OxQL queries in one request.";

    /// <summary>The description of the batch route.</summary>
    public const string BatchDescription =
        "Runs the queries in order under one time ceiling and answers each in its place of `results`: a page, or the refusal of that query. The status is 200 unless the batch itself is refused." + Pointers + Contract;

    /// <summary>The summary of the explain route.</summary>
    public const string ExplainSummary = "Explains an OxQL query without running it.";

    /// <summary>The description of the explain route.</summary>
    public const string ExplainDescription =
        "Says whether a query binds and can run on this host, with every error, the stages, the shape of the rows and, on request, the plan. The body is a query or an envelope around one. A query that does not bind is answered 200 with `valid: false`. The answer is described in " + Operations + "." + Contract;

    /// <summary>The summary of the health route.</summary>
    public const string HealthSummary = "The engine's version, contract, capabilities and limits on this host.";

    /// <summary>The description of the health route.</summary>
    public const string HealthDescription =
        "Anonymous and always 200. Publishes the limits a request is checked against, what this host can do, the state of the services its model joins into and of its model. A request with `" + OxQLQueryService.ContractHeader + ": 2` reads every member; one without the header reads the members contract 1 has. The members are described in " + Operations + ".";
}
