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
    /// <summary>
    /// Every remote target of every reference case of the model, in entity, path and declaration
    /// order, each (path, target entity) once: on a root member, on a member of an embedded
    /// object (<c>department.id</c>) and on a member of a collection element
    /// (<c>lines.vehicleId</c>, resolvable once the collection is unwound), since a resolve or
    /// semi-join can name each of them. A typed reference lists every remote target of each of
    /// its cases, and a union every one of its targets. A dictionary's <c>*</c> path repeats its
    /// member's reference and is not listed again.
    /// </summary>
    public static IReadOnlyList<RemoteReference> Of(EntityModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var references = new List<RemoteReference>();

        foreach (var entity in model.Entities.Values)
            foreach (var path in entity.Paths)
            {
                if (!ReferenceEquals(path.Shape, path.Member))
                    continue;

                var listed = new HashSet<string>(StringComparer.Ordinal);

                foreach (var reference in path.Member.References)
                    foreach (var target in reference.Targets)
                        if (target.IsRemote && listed.Add(target.Entity))
                            references.Add(new RemoteReference(entity.Id, path.Wire, target.Entity));
            }

        return references;
    }

    /// <summary>The services the model references remotely, ordinally sorted.</summary>
    public static IReadOnlyList<string> ServicesOf(EntityModel model) =>
        Of(model).Select(reference => reference.Service).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
}
