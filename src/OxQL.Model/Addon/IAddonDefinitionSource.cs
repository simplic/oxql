namespace OxQL.Model.Addon;

/// <summary>
/// The engine's per-request view of an organisation's addon definitions. The host implements
/// it over its repository with an in-process cache; the binder reads it for every entity a
/// pipeline enters.
/// </summary>
public interface IAddonDefinitionSource
{
    /// <summary>The definitions for one entity and one organisation, retired ones included.</summary>
    ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken);
}

/// <summary>A source with no definitions: every addon key is <c>unknown</c>.</summary>
public sealed class EmptyAddonDefinitionSource : IAddonDefinitionSource
{
    /// <summary>The shared instance.</summary>
    public static readonly EmptyAddonDefinitionSource Instance = new();

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AddonDefinition>> ForEntityAsync(string entity, Guid organisation, CancellationToken cancellationToken) =>
        ValueTask.FromResult<IReadOnlyList<AddonDefinition>>([]);
}
