using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Authorization;
using OxQL.AspNetCore.Controllers;
using OxQL.AspNetCore.Filtering;
using OxQL.AspNetCore.TypeEnrichment;
using OxQL.Core.Filtering;

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
        // Resolve the effective options now so the authorization convention can be
        // registered conditionally at startup.
        var options = new OxQLEndpointOptions();
        configure?.Invoke(options);

        // Register endpoint options
        services.Configure<OxQLEndpointOptions>(opts =>
        {
            configure?.Invoke(opts);
        });

        // Needed so injected query-filter providers can read the current user/claims.
        services.AddHttpContextAccessor();

        // Register the generic query service as the non-generic interface
        services.AddScoped<IOxQLQueryService, OxQLQueryService<T>>();

        // Apply any DI-registered JsonConverters (e.g. BsonDocumentJsonConverter from
        // AddOxQLMongo) to both the MVC and minimal-API JSON option paths.
        services.AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>()
            .Configure<IEnumerable<JsonConverter>>((opts, converters) =>
            {
                foreach (var c in converters)
                    opts.JsonSerializerOptions.Converters.Add(c);
            });
        services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<IEnumerable<JsonConverter>>((opts, converters) =>
            {
                foreach (var c in converters)
                    opts.SerializerOptions.Converters.Add(c);
            });

        // Add the controller assembly so MVC discovers OxQLController
        var mvcBuilder = services.AddControllers()
            .AddApplicationPart(typeof(OxQLController).Assembly)
            .AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

        // Optionally protect the OxQL controller with authorization. Applied as an
        // application-model convention so the controller stays attribute-free and the
        // protection remains fully configurable via OxQLEndpointOptions.
        if (options.RequireAuthorization)
        {
            mvcBuilder.AddMvcOptions(mvcOptions =>
                mvcOptions.Conventions.Add(new OxQLAuthorizationConvention(options)));
        }

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

        // Needed so injected query-filter providers can read the current user/claims.
        services.AddHttpContextAccessor();

        // Apply any DI-registered JsonConverters (e.g. BsonDocumentJsonConverter from
        // AddOxQLMongo) to the minimal-API JSON option path.
        services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<IEnumerable<JsonConverter>>((opts, converters) =>
            {
                foreach (var c in converters)
                    opts.SerializerOptions.Converters.Add(c);
            });

        services.AddScoped<IOxQLQueryService, OxQLQueryService<T>>();

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IOxQLQueryFilterProvider"/>
    /// onto every OxQL query. All registered providers are combined with the caller's query
    /// using a root-level logical <c>AND</c>, which is ideal for multi-tenant scenarios where an
    /// <c>OrganizationId</c> (or similar) constraint must always apply.
    /// </summary>
    /// <typeparam name="TProvider">The provider implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLQueryFilter<TProvider>(this IServiceCollection services)
        where TProvider : class, IOxQLQueryFilterProvider
    {
        services.AddScoped<IOxQLQueryFilterProvider, TProvider>();
        return services;
    }

    /// <summary>
    /// Registers an inline <see cref="IOxQLQueryFilterProvider"/> from a delegate that resolves the
    /// filters to inject for the current request (for example from the authenticated user's claims).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="filterFactory">
    /// A callback that returns the filters to inject for the supplied
    /// <see cref="OxQLFilterInjectionContext"/>. Return an empty list to inject nothing.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLQueryFilter(
        this IServiceCollection services,
        Func<OxQLFilterInjectionContext, IReadOnlyList<InjectedFilter>> filterFactory)
    {
        ArgumentNullException.ThrowIfNull(filterFactory);

        services.AddScoped<IOxQLQueryFilterProvider>(_ =>
            new DelegateOxQLQueryFilterProvider((ctx, _) =>
                ValueTask.FromResult(filterFactory(ctx))));

        return services;
    }

    /// <summary>
    /// Registers an inline asynchronous <see cref="IOxQLQueryFilterProvider"/> from a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="filterFactory">
    /// An asynchronous callback that returns the filters to inject for the supplied
    /// <see cref="OxQLFilterInjectionContext"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLQueryFilter(
        this IServiceCollection services,
        Func<OxQLFilterInjectionContext, CancellationToken, ValueTask<IReadOnlyList<InjectedFilter>>> filterFactory)
    {
        ArgumentNullException.ThrowIfNull(filterFactory);

        services.AddScoped<IOxQLQueryFilterProvider>(_ =>
            new DelegateOxQLQueryFilterProvider(filterFactory));

        return services;
    }

    /// <summary>
    /// Registers an <see cref="IOxQLTypeEnricher"/> that appends extra properties to extendable
    /// type descriptors when the <c>/types</c> endpoint is called.
    /// </summary>
    /// <typeparam name="TEnricher">The enricher implementation type.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLTypeEnricher<TEnricher>(this IServiceCollection services)
        where TEnricher : class, IOxQLTypeEnricher
    {
        services.AddScoped<IOxQLTypeEnricher, TEnricher>();
        return services;
    }

    /// <summary>
    /// Registers an inline synchronous <see cref="IOxQLTypeEnricher"/> from a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="enricherFactory">
    /// A callback that returns the extra properties for the supplied
    /// <see cref="OxQLTypeEnrichmentContext"/>. Return an empty list to contribute nothing.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLTypeEnricher(
        this IServiceCollection services,
        Func<OxQLTypeEnrichmentContext, IReadOnlyList<OxQL.AspNetCore.Models.OxQLPropertyDescriptor>> enricherFactory)
    {
        ArgumentNullException.ThrowIfNull(enricherFactory);

        services.AddScoped<IOxQLTypeEnricher>(_ =>
            new DelegateOxQLTypeEnricher((ctx, _) =>
                ValueTask.FromResult(enricherFactory(ctx))));

        return services;
    }

    /// <summary>
    /// Registers an inline asynchronous <see cref="IOxQLTypeEnricher"/> from a delegate.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="enricherFactory">
    /// An asynchronous callback that returns the extra properties for the supplied
    /// <see cref="OxQLTypeEnrichmentContext"/>.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLTypeEnricher(
        this IServiceCollection services,
        Func<OxQLTypeEnrichmentContext, CancellationToken, ValueTask<IReadOnlyList<OxQL.AspNetCore.Models.OxQLPropertyDescriptor>>> enricherFactory)
    {
        ArgumentNullException.ThrowIfNull(enricherFactory);

        services.AddScoped<IOxQLTypeEnricher>(_ =>
            new DelegateOxQLTypeEnricher(enricherFactory));

        return services;
    }
}
