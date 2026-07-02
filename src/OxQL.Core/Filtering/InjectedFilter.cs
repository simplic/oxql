namespace OxQL.Core.Filtering;

/// <summary>
/// Describes a single filter condition to force onto every query, together with the
/// per-request value it should match against.
/// <para>
/// The value is passed by <see cref="VariableName"/> reference rather than as a literal so
/// that the query-plan cache — which keys plans by filter shape and resolves variable values
/// per request — stays correct in multi-tenant scenarios.
/// </para>
/// </summary>
public sealed record InjectedFilter
{
    /// <summary>
    /// The document field path to filter on, e.g. <c>OrganizationId</c> or <c>tenant.id</c>.
    /// </summary>
    public required string Path { get; init; }

    /// <summary>
    /// The comparison operator, e.g. <c>eq</c>, <c>in</c>. Defaults to <c>eq</c>.
    /// </summary>
    public string Op { get; init; } = "eq";

    /// <summary>
    /// The name of the query variable that carries <see cref="Value"/>. Defaults to a name
    /// derived from <see cref="Path"/> when constructed through <see cref="Create"/>.
    /// </summary>
    public required string VariableName { get; init; }

    /// <summary>
    /// The value the field must match for the current request (for example the caller's
    /// organization id resolved from claims).
    /// </summary>
    public object? Value { get; init; }

    /// <summary>
    /// Creates an injected equality filter, deriving a safe variable name from the path.
    /// </summary>
    /// <param name="path">The field path to constrain.</param>
    /// <param name="value">The value to match for the current request.</param>
    /// <param name="op">The operator to use. Defaults to <c>eq</c>.</param>
    public static InjectedFilter Create(string path, object? value, string op = "eq")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return new InjectedFilter
        {
            Path = path,
            Op = op,
            Value = value,
            VariableName = DeriveVariableName(path)
        };
    }

    /// <summary>
    /// Turns a field path into a variable name that is legal in the OxQL wire format
    /// (paths may not contain <c>$</c>; variable names should avoid dots).
    /// </summary>
    private static string DeriveVariableName(string path) =>
        "__oxql_" + path.Replace('.', '_');
}
