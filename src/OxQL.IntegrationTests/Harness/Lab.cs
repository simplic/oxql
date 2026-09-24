using OxQL.IntegrationTests.Fixtures;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Harness;

/// <summary>
/// The shortest way in: a client for a service of the shared, seeded fleet.
/// <code>
/// var transport = await Lab.ClientAsync(LabService.Transport);
/// var answer = await transport.SendAsync(Corpus.Shipment, """[{ "sort": [{ "id": "asc" }] }]""");
/// </code>
/// </summary>
public static class Lab
{
    /// <summary>A client of the shared fleet, as organisation A under contract 2 unless told otherwise.</summary>
    public static async Task<LabClient> ClientAsync(LabService service, Org org = Org.A, int? contract = 2) =>
        (await CorpusFleet.SharedAsync()).Client(service, org, contract);

    /// <summary>The client of the service that owns a corpus entity.</summary>
    public static Task<LabClient> ClientForAsync(string entityId, Org org = Org.A, int? contract = 2) =>
        ClientAsync(Corpus.Entity(entityId).Service, org, contract);
}
