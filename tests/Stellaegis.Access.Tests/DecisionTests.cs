using Stellaegis.Primitives;
using static Stellaegis.Access.Tests.Fixtures;

namespace Stellaegis.Access.Tests;

/// <summary>The three steps of a decision, for a person and for a client, in a compartment and in the shared space.</summary>
public sealed class DecisionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void AssertRefused(Result result, string code)
    {
        Assert.True(result.IsFailure);
        Assert.Equal(code, result.Error!.Code);
        Assert.Equal(ErrorType.Forbidden, result.Error.Type);
    }

    [Fact]
    public async Task A_person_in_a_compartment_needs_a_membership_then_the_right()
    {
        using var host = new TestHost();

        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.DeniedCode);

        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);
        Assert.True((await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.DeniedCode);

        // The right is in acme; globex is another compartment, with its own membership.
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Globex, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_right_without_a_membership_grants_nothing()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);

        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_person_in_the_shared_space_is_an_operator_by_any_admin_right()
    {
        using var host = new TestHost();

        AssertRefused(await host.Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity)], Ct);
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct), AccessErrors.DeniedCode);

        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(ItemsRead.Identity), new(UsersRead.Identity)], Ct);
        Assert.True((await host.Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct)).IsSuccess);

        // Being an operator of the platform admits nobody in a compartment.
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_client_is_admitted_in_a_space_by_any_right_there_and_nowhere_else()
    {
        using var host = new TestHost();
        var desktop = Actor.Parse(Desktop);

        AssertRefused(await host.Decision.DecideAsync(desktop, Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);

        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        Assert.True((await host.Decision.DecideAsync(desktop, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await host.Decision.DecideAsync(desktop, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.DeniedCode);
        AssertRefused(await host.Decision.DecideAsync(desktop, Globex, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        AssertRefused(await host.Decision.DecideAsync(desktop, Space.Shared, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_client_of_the_platform_holds_its_rights_in_the_shared_space()
    {
        using var host = new TestHost();
        var inventory = Actor.Parse(Inventory);
        await host.Projection.ReplaceClientGrantsAsync(Inventory, Space.Shared, [UsersRead.Identity], Ct);

        Assert.True((await host.Decision.DecideAsync(inventory, Space.Shared, UsersRead, secondFactor: false, Ct)).IsSuccess);
        AssertRefused(await host.Decision.DecideAsync(inventory, Acme, UsersRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
    }

    [Fact]
    public async Task A_client_acting_for_a_person_gets_the_intersection_of_their_rights()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity), new(ItemsWrite.Identity)], Ct);
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [ItemsRead.Identity], Ct);
        var janeViaDesktop = JaneActor.Through(Actor.Parse(Desktop));

        Assert.True((await host.Decision.DecideAsync(janeViaDesktop, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
        // Jane may write; the desktop may not: the client restricts, never widens.
        AssertRefused(await host.Decision.DecideAsync(janeViaDesktop, Acme, ItemsWrite, secondFactor: false, Ct), AccessErrors.ViaDeniedCode);
        // The person's own refusal comes first.
        AssertRefused(await host.Decision.DecideAsync(janeViaDesktop, Acme, UsersRead, secondFactor: false, Ct), AccessErrors.DeniedCode);
    }

    [Fact]
    public async Task A_via_without_any_right_is_refused_even_for_a_job()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [new(ItemsRead.Identity)], Ct);

        AssertRefused(await host.Decision.DecideAsync(JaneActor.Through(Actor.Job("import")), Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.ViaDeniedCode);

        await host.Projection.ReplaceClientGrantsAsync("job:import", Acme, [ItemsRead.Identity], Ct);
        Assert.True((await host.Decision.DecideAsync(JaneActor.Through(Actor.Job("import")), Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_caller_may_name_the_kind_of_the_subject_itself()
    {
        using var host = new TestHost();
        // A client whose id happens to be a GUID: by its form it would be read as a person.
        var clientId = Guid.NewGuid().ToString("N");
        await host.Projection.ReplaceClientGrantsAsync(clientId, Acme, [ItemsRead.Identity], Ct);

        AssertRefused(await host.Decision.DecideAsync(Actor.Parse(clientId), Acme, ItemsRead, secondFactor: false, Ct), AccessErrors.NotMemberCode);
        Assert.True((await host.Decision.DecideAsync(AccessSubject.Client(clientId), null, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
    }
    [Fact]
    public async Task A_right_that_requires_a_second_factor_is_refused_to_a_session_without_one_and_said_only_to_its_holder()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);

        // Not held: refused as any operation not held, whatever the session proved.
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, PayrollPay, secondFactor: false, Ct), AccessErrors.DeniedCode);

        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [AccessGrant.Of(PayrollPay), AccessGrant.Of(ItemsRead)], Ct);
        AssertRefused(await host.Decision.DecideAsync(JaneActor, Acme, PayrollPay, secondFactor: false, Ct), AccessErrors.SecondFactorRequiredCode);
        Assert.True((await host.Decision.DecideAsync(JaneActor, Acme, PayrollPay, secondFactor: true, Ct)).IsSuccess);

        // The requirement is the operation's: the other right of the same snapshot opens without one.
        Assert.True((await host.Decision.DecideAsync(JaneActor, Acme, ItemsRead, secondFactor: false, Ct)).IsSuccess);
    }

    [Fact]
    public async Task In_the_shared_space_a_right_that_requires_a_second_factor_is_refused_without_one()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceAdminGrantsAsync(Jane, [new(UsersRead.Identity, RequiresSecondFactor: true)], Ct);

        AssertRefused(await host.Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: false, Ct), AccessErrors.SecondFactorRequiredCode);
        Assert.True((await host.Decision.DecideAsync(JaneActor, Space.Shared, UsersRead, secondFactor: true, Ct)).IsSuccess);
    }

    [Fact]
    public async Task Through_a_client_the_second_factor_is_the_persons_and_the_client_must_still_hold_the_operation()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceMembershipsAsync(Jane, [Acme], Ct);
        await host.Projection.ReplaceUserGrantsAsync(Jane, Acme, [AccessGrant.Of(PayrollPay)], Ct);
        var throughDesktop = JaneActor.Through(Actor.Parse(Desktop));

        AssertRefused(await host.Decision.DecideAsync(throughDesktop, Acme, PayrollPay, secondFactor: false, Ct), AccessErrors.SecondFactorRequiredCode);
        AssertRefused(await host.Decision.DecideAsync(throughDesktop, Acme, PayrollPay, secondFactor: true, Ct), AccessErrors.ViaDeniedCode);

        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [PayrollPay.Identity], Ct);
        Assert.True((await host.Decision.DecideAsync(throughDesktop, Acme, PayrollPay, secondFactor: true, Ct)).IsSuccess);
    }

    [Fact]
    public async Task A_client_has_no_second_factor_and_its_rights_never_ask_for_one()
    {
        using var host = new TestHost();
        await host.Projection.ReplaceClientGrantsAsync(Desktop, Acme, [PayrollPay.Identity], Ct);

        Assert.True((await host.Decision.DecideAsync(AccessSubject.Client(Desktop), null, Acme, PayrollPay, secondFactor: false, Ct)).IsSuccess);
    }
}

/// <summary>The rule that reads a subject from an actor's form.</summary>
public sealed class SubjectTests
{
    [Fact]
    public void A_guid_is_a_person_anything_else_a_client()
    {
        Assert.Equal(AccessSubject.Person(Jane), AccessSubject.Of(JaneActor));
        Assert.Equal(AccessSubject.Person(JaneId), AccessSubject.Of(Actor.Parse(JaneId.ToString("D"))));
        Assert.Equal(AccessSubject.Client(Desktop), AccessSubject.Of(Actor.Parse(Desktop)));
        Assert.Equal(AccessSubject.Client("job:purge"), AccessSubject.Of(Actor.Job("purge")));
        // The via is not read: the subject is the one who answers.
        Assert.Equal(AccessSubject.Person(Jane), AccessSubject.Of(JaneActor.Through(Actor.Parse(Desktop))));
        Assert.Equal(JaneActor, AccessSubject.Person(Jane).ToActor());
    }

    [Fact]
    public void A_subject_needs_an_identifier()
    {
        Assert.Throws<ArgumentException>(() => AccessSubject.Person(" "));
        Assert.Throws<ArgumentException>(() => AccessSubject.Client(""));
        Assert.Throws<ArgumentException>(() => AccessSubject.Person(Guid.Empty));
    }

}
