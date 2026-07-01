using Microsoft.Extensions.DependencyInjection;

namespace OxQL.Studio;

/// <summary>
/// Extension methods for registering the OxQL Studio UI services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OxQL Studio query-builder UI services.
    /// <para>
    /// Call <c>app.MapOxQLStudio()</c> after building the application to expose the UI
    /// (default route <c>/oxql</c>).
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="OxQLStudioOptions"/>.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddOxQLStudio(
        this IServiceCollection services,
        Action<OxQLStudioOptions>? configure = null)
    {
        var options = new OxQLStudioOptions();
        configure?.Invoke(options);
        options.Normalize();

        services.AddSingleton(options);

        return services;
    }
}
