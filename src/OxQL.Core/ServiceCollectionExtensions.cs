using OxQL.Core.Caching;
using OxQL.Core.Cursor;
using OxQL.Core.Interfaces;
using OxQL.Core.Models;
using OxQL.Core.Normalization;
using OxQL.Core.Planning;
using OxQL.Core.Registration;
using OxQL.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace OxQL.Core;

/// <summary>
/// Extension methods for registering OxQL.Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds OxQL.Core services to the dependency injection container.
    /// </summary>
    public static IServiceCollection AddOxQLCore(
        this IServiceCollection services,
        Action<OxQLOptions>? configure = null)
    {
        var options = new OxQLOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<IQueryValidator, QueryValidator>();
        services.AddSingleton<IQueryRequestNormalizer, QueryRequestNormalizer>();
        services.AddSingleton<IQueryPlanner, QueryPlanner>();
        services.AddSingleton<IQueryPlanCache, QueryPlanCache>();
        services.AddSingleton<ICursorSerializer, CursorSerializer>();
        services.AddSingleton<OxQLTypeRegistry>();

        return services;
    }
}
