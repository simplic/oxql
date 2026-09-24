using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OxQL.AspNetCore.Resolve;
using OxQL.Core.Binding;
using OxQL.Core.Engine;
using OxQL.Model;
using OxQL.Model.Attributes;
using OxQL.Model.Build;
using OxQL.Tests.AspNetCore;
using OxQL.Tests.Bind;
using OxQL.Tests.Execute;
using Xunit;

namespace OxQL.Tests.Core;

/// <summary>
/// The remote references of a model at every depth: a reference on a member of an embedded
/// object or of a collection element needs its service configured as much as one on a root
/// member, so the startup check and health see all of them.
/// </summary>
public class RemoteReferencesTests
{
    private const string Order = "nest.order";

    private static readonly EntityModel Model = ClrModelBuilder.Build(
    [
        new EntityDeclaration(Order, Order, typeof(OrderModel), "tmp_nest_orders", null, false),
    ]);

    [Fact]
    public void The_model_builds_without_findings() =>
        Model.Findings.Should().BeEmpty();

    [Fact]
    public void Every_depth_is_listed_once_in_path_order()
    {
        RemoteReferences.Of(Model).Select(reference => $"{reference.Entity}#{reference.Path}->{reference.TargetEntity}")
            .Should().Equal(
                "nest.order#vehicleId->vehicle.vehicle",
                "nest.order#department.id->fleet.department",
                "nest.order#department.manager.employeeId->staff.employee",
                "nest.order#lines.contactNumber->crm.contact",
                "nest.order#approvers->staff.employee");
    }

    [Fact]
    public void The_services_are_every_depth_s_distinct_and_sorted() =>
        RemoteReferences.ServicesOf(Model).Should().Equal("crm", "fleet", "staff", "vehicle");

    [Fact]
    public void A_dictionary_reference_is_listed_once_under_its_member()
    {
        var model = ClrModelBuilder.Build([new EntityDeclaration("nest.board", "nest.board", typeof(BoardModel), "tmp_nest_boards", null, false)]);

        model.Entities["nest.board"].Paths.Select(path => path.Wire).Should().Contain("seats.*", "the dictionary's value path carries the member's reference too");
        RemoteReferences.Of(model).Select(reference => reference.Path).Should().Equal("seats");
    }

    [Fact]
    public async Task A_nested_reference_resolves_remotely()
    {
        var bound = await BindHost.BoundAsync(Model, Order, """[{ "resolve": { "path": "department.id", "as": "dept" } }]""");

        bound.Stages.OfType<BoundStage.Resolve>().Should().ContainSingle()
            .Which.Should().Match<BoundStage.Resolve>(resolve => resolve.IsRemote && resolve.TargetEntity == "fleet.department");
    }

    [Fact]
    public async Task A_reference_in_a_collection_element_resolves_remotely_once_unwound()
    {
        var bound = await BindHost.BoundAsync(Model, Order,
            """[{ "unwind": { "path": "lines" } }, { "resolve": { "path": "lines.contactNumber", "as": "contact" } }]""");

        bound.Stages.OfType<BoundStage.Resolve>().Should().ContainSingle()
            .Which.Should().Match<BoundStage.Resolve>(resolve => resolve.IsRemote && resolve.TargetEntity == "crm.contact");
    }

    [Fact]
    public void The_check_names_the_nested_and_the_collection_references()
    {
        var findings = RemoteReferenceCheck.Unconfigured(Model, new FakeRemoteClient { Configured = ["vehicle", "staff"] });

        findings.Select(finding => $"{finding.Path}->{finding.Service}").Should().Equal("department.id->fleet", "lines.contactNumber->crm");
    }

    [Fact]
    public void A_nested_reference_into_an_unconfigured_service_stops_a_strict_host()
    {
        using var host = new SampleHost(configure: services =>
            services.AddSingleton<IRemoteQueryClient>(new FakeRemoteClient { Configured = ["vehicle", "staff", "crm"] }))
        {
            Model = Model,
            Environment = "Development",
        };

        var act = () => host.CreateClient();

        act.Should().Throw<InvalidOperationException>().WithMessage("*nest.order#department.id*InternalHosts*'fleet'*");
    }

    [Fact]
    public async Task Health_lists_the_services_of_nested_and_collection_references()
    {
        var client = new FakeRemoteClient { Configured = ["vehicle", "fleet", "staff", "crm"] };

        using var host = new SampleHost(configure: services => services.AddSingleton<IRemoteQueryClient>(client))
        {
            Model = Model,
            Environment = "Production",
        };

        var response = await host.CreateClient().GetAsync("/OxQL/health");
        var body = await SampleHost.Body(response);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        body!["remote"]!.AsArray().Select(node => node!["service"]!.GetValue<string>())
            .Should().Equal("crm", "fleet", "staff", "vehicle");
    }

    public sealed class OrderModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("vehicle.vehicle", "id")]
        public Guid? VehicleId { get; set; }

        public DepartmentPart? Department { get; set; }

        public List<LinePart> Lines { get; set; } = [];

        [OxQLReference("staff.employee", "id")]
        public List<Guid> Approvers { get; set; } = [];
    }

    public sealed class BoardModel
    {
        public Guid Id { get; set; }

        public Guid OrganizationId { get; set; }

        [OxQLReference("staff.employee", "id")]
        public Dictionary<string, Guid> Seats { get; set; } = [];
    }

    public sealed class DepartmentPart
    {
        [OxQLReference("fleet.department", "id")]
        public Guid Id { get; set; }

        public ManagerPart? Manager { get; set; }
    }

    public sealed class ManagerPart
    {
        [OxQLReference("staff.employee", "id")]
        public Guid EmployeeId { get; set; }
    }

    public sealed class LinePart
    {
        [OxQLReference("crm.contact", "number")]
        public string? ContactNumber { get; set; }

        public int Quantity { get; set; }
    }
}
