using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Model.Addon;

namespace OxQL.Core;

/// <summary>Registers the engine's core services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Adds the options and the cursor codec; a backend adds the engine, the host adds the model.</summary>
    public static IServiceCollection AddOxQLCore(this IServiceCollection services, Action<OxQLOptions>? configure = null)
    {
        var options = new OxQLOptions();
        configure?.Invoke(options);

        return services.AddOxQLCore(options);
    }

    /// <summary>Adds the options bound from a configuration section (<c>OxQL</c>); the README lists every key.</summary>
    public static IServiceCollection AddOxQLCore(this IServiceCollection services, IConfiguration section, Action<OxQLOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(section);

        var options = new OxQLOptions();
        section.Bind(options);
        configure?.Invoke(options);

        // The published engine named the owner-fetch cache's budget ResolveCacheMaxEntries. The key is
        // gone; a host that still sets it is told at start that the value was not applied, not left to
        // find out that its cache runs at the default.
        if (section[FormerCacheBudgetKey] is { } former)
            services.AddSingleton(new OxQLOptionsAdjustment(
                $"OxQL:{FormerCacheBudgetKey} was {former}; the key is no longer read. The budget of the owner-fetch cache is OxQL:Cache:{nameof(CacheOptions.OwnerFetchCacheMaxEntries)}, now {options.Cache.OwnerFetchCacheMaxEntries}: rename the key."));

        return services.AddOxQLCore(options);
    }

    /// <summary>The configuration key the published engine read the owner-fetch cache's budget from, under the <c>OxQL</c> section.</summary>
    public const string FormerCacheBudgetKey = "Cache:ResolveCacheMaxEntries";

    private static IServiceCollection AddOxQLCore(this IServiceCollection services, OxQLOptions options)
    {
        // The limits are brought into range here rather than trusted: this is the one place
        // every configuration passes through. Each adjustment is registered so the engine can
        // log it when it is built.
        foreach (var adjustment in options.Normalise())
            services.AddSingleton(new OxQLOptionsAdjustment(adjustment));

        services.AddSingleton(options);
        services.AddSingleton(provider => new CursorCodec(provider.GetRequiredService<OxQLOptions>().Cursor.SigningKey));
        services.TryAddSingleton<IAddonDefinitionSource>(EmptyAddonDefinitionSource.Instance);

        // One record of the owners that did not answer per host: the keyed fetch and the health probe share it.
        services.TryAddSingleton(provider => new OwnerAvailability(provider.GetRequiredService<OxQLOptions>(), provider.GetService<TimeProvider>()));

        // One check of the declared remote references per host: health and explain read the same result.
        services.TryAddSingleton(provider => new RemoteReferenceValidator(provider.GetService<TimeProvider>()));

        return services;
    }
}

/// <summary>One limit the registration adjusted; the engine logs each as a warning when it is built.</summary>
public sealed record OxQLOptionsAdjustment(string Message);
