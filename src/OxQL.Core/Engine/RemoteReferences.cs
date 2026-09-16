using OxQL.Model;

namespace OxQL.Core.Engine;

/// <summary>One declared reference into another service: where it is declared and which service owns the target.</summary>
public sealed record RemoteReference(string Entity, string Path, string TargetEntity)
{
    /// <summary>The owning service: the target's namespace, the host's <c>InternalHosts</c> key.</summary>
    public string Service => TargetEntity.Split('.')[0];
}

/// <summary>The references of a model that leave the host: what a resolve or semi-join needs a remote query client for.</summary>
public static class RemoteReferences
{
    /// <summary>Every remote reference of the model, in entity and path order.</summary>
    public static IReadOnlyList<RemoteReference> Of(EntityModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var references = new List<RemoteReference>();

        foreach (var entity in model.Entities.Values)
            foreach (var path in entity.Paths)
                if (path.Member.Reference is { IsRemote: true } reference && path.Depth == 0)
                    references.Add(new RemoteReference(entity.Id, path.Wire, reference.TargetEntity));

        return references;
    }

    /// <summary>The services the model references remotely, ordinally sorted.</summary>
    public static IReadOnlyList<string> ServicesOf(EntityModel model) =>
        Of(model).Select(reference => reference.Service).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
