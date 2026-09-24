using OxQL.Model;
using OxQL.Model.Build;

namespace OxQL.IntegrationTests.Fleet;

/// <summary>
/// One simulated service of the fleet: its key, which is the namespace of its entity ids and
/// the key another host reaches it under, and the model built from its entities alone. Each
/// host sees only its own entities, so a reference into another service is remote exactly as
/// it is between real services.
/// </summary>
public sealed class LabService
{
    public static readonly LabService Staff = new("staff");

    public static readonly LabService Fleet = new("fleet");

    public static readonly LabService Transport = new("transport");

    public static readonly LabService Ledger = new("ledger");

    public static readonly LabService Conformance = new("conformance");

    public static readonly IReadOnlyList<LabService> All = [Staff, Fleet, Transport, Ledger, Conformance];

    private readonly Lazy<EntityModel> model;

    private LabService(string key)
    {
        Key = key;
        model = new Lazy<EntityModel>(Build, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string Key { get; }

    /// <summary>The service's model; built once per run, after the storage conventions are registered.</summary>
    public EntityModel Model => model.Value;

    public static LabService Of(string key) =>
        All.FirstOrDefault(service => service.Key == key) ?? throw new ArgumentException($"'{key}' is not a lab service.", nameof(key));

    public override string ToString() => Key;

    private EntityModel Build()
    {
        var findings = new List<BuildFinding>();
        var declarations = EntityScanner.Scan([typeof(LabService).Assembly], findings)
            .Where(declaration => declaration.Id.StartsWith(Key + ".", StringComparison.Ordinal))
            .ToList();

        if (declarations.Count == 0)
            throw new InvalidOperationException($"The lab service '{Key}' declares no entities.");

        return ClrModelBuilder.Build(declarations);
    }
}
