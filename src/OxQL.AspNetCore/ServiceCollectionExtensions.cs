using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Controllers;

namespace OxQL.AspNetCore;

/// <summary>
/// Extension methods for registering OxQL ASP.NET Core services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the OxQL query controller and supporting services to the DI container.
    /// <para>
    /// Requires that <c>AddOxQLCore()</c> and a backend adapter (e.g. <c>AddOxQLMongo()</c>)
    /// have already been registered so that <see cref="OxQL.Core.Interfaces.IQueryExecutor{T}"/>
    /// is available.
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// The document type returned by the registered <see cref="OxQL.Core.Interfaces.IQueryExecutor{T}"/>.
    /// For MongoDB this is typically <c>BsonDocument</c>.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="OxQLEndpointOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLAspNetCore<T>(
        this IServiceCollection services,
        Action<OxQLEndpointOptions>? configure = null)
    {
        // Register endpoint options
        services.Configure<OxQLEndpointOptions>(opts =>
        {
            configure?.Invoke(opts);
        });

        // Register the generic query service as the non-generic interface
        services.AddScoped<IOxQLQueryService, OxQLQueryService<T>>();

        // Add the controller assembly so MVC discovers OxQLController
        services.AddControllers()
            .AddApplicationPart(typeof(OxQLController).Assembly)
            .AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

        return services;
    }

    /// <summary>
    /// Adds the OxQL query endpoint as a minimal-API route group.
    /// Use this instead of <see cref="AddOxQLAspNetCore{T}"/> when you prefer
    /// minimal APIs over controllers.
    /// <para>
    /// Requires that <c>AddOxQLCore()</c> and a backend adapter have been registered.
    /// </para>
    /// </summary>
    /// <typeparam name="T">
    /// The document type returned by the registered <see cref="OxQL.Core.Interfaces.IQueryExecutor{T}"/>.
    /// </typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="OxQLEndpointOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLEndpoint<T>(
        this IServiceCollection services,
        Action<OxQLEndpointOptions>? configure = null)
    {
        services.Configure<OxQLEndpointOptions>(opts =>
        {
            configure?.Invoke(opts);
        });

        services.AddScoped<IOxQLQueryService, OxQLQueryService<T>>();

        return services;
    }
}
