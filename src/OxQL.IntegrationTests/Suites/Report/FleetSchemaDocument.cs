using System.Text.Json.Nodes;
using OxQL.Core.Models;
using OxQL.IntegrationTests.Fleet;

namespace OxQL.IntegrationTests.Suites.Report;

/// <summary>
/// The Ox Schema document (format 1.1) a fleet service would publish on <c>GET /schema</c>, for the
/// studio's fixtures and for the tests that read an explain answer against it: the fleet's hosts run
/// the engine without the base package, so nothing serves the route (<see cref="SchemaDocumentWriter"/>).
/// </summary>
internal static class FleetSchemaDocument
{
    public static JsonObject Of(LabService service, OxQLOptions? options = null) => SchemaDocumentWriter.Of(service.Model, service.Key, options);
}
