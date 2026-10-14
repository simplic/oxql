using Xunit;

namespace OxQL.IntegrationTests.Perf;

/// <summary>
/// A fact that runs only when every one of the named environment variables is set. Without them it
/// is reported as skipped, with the variables to set, and never as passed.
/// </summary>
internal sealed class OptInFactAttribute : FactAttribute
{
    /// <summary>The variable that turns on the load and soak tests: they run for seconds and measure under contention.</summary>
    public const string Load = "OXQL_LOAD";

    public OptInFactAttribute(params string[] variables)
    {
        if (variables.Any(variable => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable))))
            Skip = $"Opt-in: set {string.Join(" and ", variables)} to run it.";
    }
}
