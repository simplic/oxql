using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.AspNetCore.Authorization;
using OxQL.AspNetCore.Controllers;
using OxQL.AspNetCore.Resolve;
using OxQL.AspNetCore.Scope;

namespace OxQL.AspNetCore;

/// <summary>Registers the query controller and the host contracts.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the query controller. Needs <c>AddOxQLCore</c>, a backend (<c>AddOxQLMongo</c>) and an
    /// <see cref="IOxQLScopeProvider"/>; without a scope provider the host fails on startup, so a
    /// service cannot run the engine unscoped.
    /// </summary>
    /// <typeparam name="T">Kept for source compatibility with the v1 registration; the engine no longer depends on the document type.</typeparam>
    public static IServiceCollection AddOxQLAspNetCore<T>(this IServiceCollection services, Action<OxQLEndpointOptions>? configure = null) =>
        services.AddOxQLAspNetCore(configure);

    /// <summary>Adds the query controller.</summary>
    public static IServiceCollection AddOxQLAspNetCore(this IServiceCollection services, Action<OxQLEndpointOptions>? configure = null)
    {
        var options = new OxQLEndpointOptions();
        configure?.Invoke(options);

        services.Configure<OxQLEndpointOptions>(opts => configure?.Invoke(opts));
        services.AddHttpContextAccessor();
        services.AddScoped<IOxQLQueryService, OxQLQueryService>();
        services.AddTransient<IStartupFilter, ScopeProviderStartupFilter>();
        services.AddTransient<IStartupFilter, RemoteReferenceStartupFilter>();

        services.AddOptions<Microsoft.AspNetCore.Mvc.JsonOptions>()
            .Configure<IEnumerable<JsonConverter>>((opts, converters) =>
            {
                foreach (var converter in converters)
                    opts.JsonSerializerOptions.Converters.Add(converter);
            });
        services.AddOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>()
            .Configure<IEnumerable<JsonConverter>>((opts, converters) =>
            {
                foreach (var converter in converters)
                    opts.SerializerOptions.Converters.Add(converter);
            });

        var mvcBuilder = services.AddControllers()
            .AddApplicationPart(typeof(OxQLController).Assembly)
            .AddJsonOptions(opts =>
            {
                opts.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                opts.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            });

        if (options.RequireAuthorization)
            mvcBuilder.AddMvcOptions(mvcOptions => mvcOptions.Conventions.Add(new OxQLAuthorizationConvention(options)));

        return services;
    }

    /// <summary>Registers the scope provider: the one place the engine learns the caller's organisation.</summary>
    public static IServiceCollection AddOxQLScope<TProvider>(this IServiceCollection services)
        where TProvider : class, IOxQLScopeProvider
    {
        services.TryAddScoped<IOxQLScopeProvider, TProvider>();
        return services;
    }

    /// <summary>Registers a scope provider from a delegate.</summary>
    public static IServiceCollection AddOxQLScope(this IServiceCollection services, Func<HttpContext?, Guid?> organisation)
    {
        ArgumentNullException.ThrowIfNull(organisation);

        services.TryAddScoped<IOxQLScopeProvider>(_ => new DelegateOxQLScopeProvider(organisation));
        return services;
    }

    /// <summary>Refuses to start when no scope provider is registered.</summary>
    private sealed class ScopeProviderStartupFilter(IServiceProvider provider) : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next)
        {
            using var scope = provider.CreateScope();

            if (scope.ServiceProvider.GetService<IOxQLScopeProvider>() is null)
                throw new InvalidOperationException(
                    "OxQL refuses to start without an IOxQLScopeProvider: register one with AddOxQLScope so every query is scoped to the caller's organisation.");

            return next;
        }
    }
}
