using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OxQL.Core.Cursor;
using OxQL.Core.Engine;
using OxQL.Core.Models;
using OxQL.Core.Registration;
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

        return services.AddOxQLCore(options);
    }

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
        services.AddSingleton<OxQLTypeRegistry>();

        return services;
    }
}

/// <summary>One limit the registration adjusted; the engine logs each as a warning when it is built.</summary>
public sealed record OxQLOptionsAdjustment(string Message);
