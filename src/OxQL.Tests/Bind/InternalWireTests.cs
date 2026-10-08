using System.Text.Json;
using FluentAssertions;
using OxQL.Core.Binding;
using OxQL.Core.Models;
using Xunit;

namespace OxQL.Tests.Bind;

/// <summary>
/// The internal route between two services is written by their engines, not by a caller. A top-level
/// member an owner does not know there is another build's: the owner refuses it with the code it
/// always had and says the cause, so the origin's <c>RESOLVE_REFUSED</c> does not name a member no
/// caller wrote and leave the reader to guess. Two builds of the engine are never deployed apart;
/// while they are, this is what the calls between them answer.
/// </summary>
public class InternalWireTests
{
    private const string Order = "probe.order";

    private static async Task<QueryValidationError> Refused(string request, bool internalCall)
    {
        var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse(request), BindHost.Context() with { Internal = internalCall });

        return outcome.Should().BeOfType<BindOutcome.Failed>(BindHost.Describe(outcome)).Subject.Refusal.Errors.Should().ContainSingle().Subject;
    }

    [Fact]
    public async Task A_member_another_build_writes_on_the_internal_route_is_refused_with_its_cause()
    {
        var error = await Refused($$"""{ "entityType": "{{Order}}", "trace": true, "hint": { "index": "x" }, "pipeline": [{ "page": { "limit": 1 } }] }""", internalCall: true);

        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Message.Should().StartWith("'trace, hint' is not a member of a request; a request carries " + Binder.InternalRequestMembers + ".").And.EndWith(Binder.OtherBuildHint);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"reason":"engineBuild","members":["trace","hint"]}""");
    }

    [Fact]
    public async Task So_is_a_member_of_keyedBy_this_engine_does_not_know()
    {
        var error = await Refused($$"""{ "entityType": "{{Order}}", "keyedBy": { "path": "id", "keys": [], "shape": "wide" }, "pipeline": [] }""", internalCall: true);

        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Message.Should().StartWith("'keyedBy.shape' is not a member of keyedBy").And.EndWith(Binder.OtherBuildHint);
        JsonSerializer.Serialize(error.Params).Should().Be("""{"reason":"engineBuild","members":["keyedBy.shape"]}""");
    }

    [Fact]
    public async Task On_the_public_route_the_refusal_is_what_it_was()
    {
        var error = await Refused($$"""{ "entityType": "{{Order}}", "trace": true, "pipeline": [{ "page": { "limit": 1 } }] }""", internalCall: false);

        error.Code.Should().Be(Codes.UnknownRequestMember);
        error.Message.Should().Be("'trace' is not a member of a request; a request carries " + Binder.RequestMembers + ".");
        error.Params.Should().BeNull();
    }

    [Fact]
    public async Task The_members_this_build_writes_are_known_whatever_they_hold()
    {
        // What an origin of this build adds to an owner query: the breakdown's request.
        var outcome = await BindHost.BindAsync(BindHost.Probe, BindHost.Parse($$"""{ "entityType": "{{Order}}", "timing": true, "maxTimeMs": 500, "pipeline": [{ "page": { "limit": 1 } }] }"""), BindHost.Context() with { Internal = true });

        outcome.Should().BeOfType<BindOutcome.Bound>(BindHost.Describe(outcome));
    }
}
