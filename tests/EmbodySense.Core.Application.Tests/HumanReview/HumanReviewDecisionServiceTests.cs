using System.Collections.Immutable;
using System.Runtime.InteropServices;
using EmbodySense.Core.Application.HumanReview;
using EmbodySense.Core.Application.HumanReview.Models;
using EmbodySense.Core.Application.Loops;
using EmbodySense.Core.Application.Loops.Models;
using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;
using EmbodySense.Core.Common.Loops.Custom.Execution;
using EmbodySense.Core.Common.Loops.Execution;
using EmbodySense.Core.Common.Loops.Execution.Models;
using EmbodySense.Core.Common.Loops.Models.Custom.Execution;

namespace EmbodySense.Core.Application.Tests.HumanReview;

public sealed class HumanReviewDecisionServiceTests
{
    [Fact]
    public async Task Invalid_bounded_client_fields_are_rejected_before_any_store_or_authorizer_access()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand("../run", fixture.Run.LifecycleVersion, "invalid-run-one", HumanReviewDecisionKind.Reject, null))).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand("con", fixture.Run.LifecycleVersion, "reserved-run-one", HumanReviewDecisionKind.Reject, null))).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand(fixture.Run.Id, -1, "invalid-version-one", HumanReviewDecisionKind.Reject, null))).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand(fixture.Run.Id, 0, "zero-version-one", HumanReviewDecisionKind.Reject, null))).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand(fixture.Run.Id, fixture.Run.LifecycleVersion, "invalid-kind-one", (HumanReviewDecisionKind)999, null))).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, (await service.DecideAsync(new HumanReviewDecisionCommand(fixture.Run.Id, fixture.Run.LifecycleVersion, "invalid-detail-one", HumanReviewDecisionKind.RequestInformation, null))).Status);
        Assert.Equal(0, store.ReadCount);
        Assert.Empty(authorizer.Requests);
    }

    [Fact]
    public async Task Approve_records_exact_bound_decision_reservation_and_preserves_review_blocked_frontier()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var atUtc = fixture.Run.UpdatedAtUtc.AddMinutes(1);
        var result = await Service(store, authorizer, atUtc).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "approve-one", HumanReviewDecisionKind.Approve));

        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, result.Status);
        var receipt = Assert.IsType<HumanReviewDecisionOperationReceipt>(result.Receipt);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, receipt.Disposition);
        Assert.Equal("authorization-one", receipt.Provenance.CorrelationId);
        var persisted = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var review = Assert.IsType<HumanReviewRunState>(persisted.HumanReview);
        var decision = Assert.IsType<HumanReviewDecision>(review.AcceptedTerminalDecision);
        Assert.Equal(HumanReviewDecisionKind.Approve, decision.Kind);
        Assert.Equal(decision.DecisionHash, review.ContinuationReservation?.Decision.DecisionHash);
        Assert.Equal(HumanReviewLifecycleStatus.Approved, review.Lifecycle.Status);
        Assert.Equal("authorization-one", review.Lifecycle.Provenance.CorrelationId);
        Assert.All(review.Evidence.Skip(1), item => Assert.Equal("authorization-one", item.Provenance.CorrelationId));
        Assert.Equal(fixture.Run.Status, persisted.Status);
        Assert.Equal(fixture.Run.Frontier?.Payload.ContentHash, persisted.Frontier?.Payload.ContentHash);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, persisted.Frontier?.Payload.Status);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid);
        var authorizationRequest = Assert.Single(authorizer.Requests);
        Assert.Equal(fixture.Request.RequestHash, authorizationRequest.RequestHash);
        Assert.Equal("approve-one", authorizationRequest.DecisionOperationId);
        Assert.Equal(receipt.ProposalHash, authorizationRequest.ProposalHash);
        Assert.Equal(atUtc, authorizationRequest.EvaluatedAtUtc);
        Assert.NotSame(store.Run?.HumanReview?.Request, authorizationRequest.Request);
    }

    [Fact]
    public async Task Authorizer_owned_scope_buffer_is_copied_before_durable_hashing_and_cannot_corrupt_the_decision_after_return()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var scopeBuffer = new[] { "scope-alpha", "scope-beta" };
        var authorizer = new HumanReviewDecisionTestAuthorizer { ScopeIds = ImmutableCollectionsMarshal.AsImmutableArray(scopeBuffer) };

        var result = await Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "scope-copy-one", HumanReviewDecisionKind.Reject));
        scopeBuffer[0] = "scope-zeta";

        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, result.Status);
        var decision = Assert.Single(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(new[] { "scope-alpha", "scope-beta" }, decision.ReviewerScopeIds.ToArray());
        Assert.True(HumanReviewContractHash.MatchesDecision(decision));
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Request_information_records_awaiting_information_without_releasing_the_frontier()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var atUtc = fixture.Run.UpdatedAtUtc.AddMinutes(1);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), atUtc).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "information-one", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));

        Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, result.Status);
        var persisted = Assert.IsType<CustomLoopRunRecord>(store.Run);
        Assert.Equal(HumanReviewLifecycleStatus.AwaitingInformation, persisted.HumanReview?.Lifecycle.Status);
        Assert.Null(persisted.HumanReview?.AcceptedTerminalDecision);
        Assert.Null(persisted.HumanReview?.ContinuationReservation);
        var action = Assert.Single(Assert.IsType<HumanReviewRunState>(persisted.HumanReview).DecisionActions);
        Assert.Equal(HumanReviewDecisionKind.RequestInformation, action.Reservation.Decision.Kind);
        Assert.Equal(persisted.HumanReview.Request.RequestHash, action.Reservation.Request.RequestHash);
        Assert.Equal(persisted.LifecycleVersion, action.ReservedLifecycleVersion);
        Assert.Null(action.Wake);
        Assert.Empty(action.Claims);
        Assert.Null(action.Completion);
        Assert.Null(action.Retirement);
        Assert.True(HumanReviewDecisionActionContractHash.MatchesState(action));
        Assert.Equal(CustomLoopRunStatus.Paused, persisted.Status);
        Assert.Equal(GovernedLoopFrontierStatus.ReviewBlocked, persisted.Frontier?.Payload.Status);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewLifecycleStatus.Rejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewLifecycleStatus.Cancelled)]
    public async Task Reject_and_cancel_record_terminal_outcomes_with_exact_nonapproval_action_reservations(HumanReviewDecisionKind kind, HumanReviewLifecycleStatus lifecycle)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "terminal-" + kind.ToString().ToLowerInvariant(), kind));

        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, result.Status);
        var persisted = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var review = Assert.IsType<HumanReviewRunState>(persisted.HumanReview);
        Assert.Equal(lifecycle, review.Lifecycle.Status);
        Assert.Equal(kind, review.AcceptedTerminalDecision?.Kind);
        Assert.Null(review.ContinuationReservation);
        var action = Assert.Single(review.DecisionActions);
        Assert.Equal(kind, action.Reservation.Decision.Kind);
        Assert.Equal(review.Request.RequestHash, action.Reservation.Request.RequestHash);
        Assert.Equal(persisted.LifecycleVersion, action.ReservedLifecycleVersion);
        Assert.Null(action.Wake);
        Assert.Empty(action.Claims);
        Assert.Null(action.Completion);
        Assert.Null(action.Retirement);
        Assert.True(HumanReviewDecisionActionContractHash.MatchesState(action));
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid);
    }

    [Fact]
    public async Task Unauthenticated_operation_fails_closed_without_mutation_or_replay_disclosure()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer { IsAuthorized = false, ActorId = null, ReviewerRoleId = null, ScopeIds = default, CorrelationId = null };
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "denied-one", HumanReviewDecisionKind.Reject);

        var first = await service.DecideAsync(command);
        var second = await service.DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, first.Status);
        Assert.Null(first.Receipt);
        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, second.Status);
        Assert.Null(second.Receipt);
        Assert.Equal(0, store.UpdateCount);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, store.Run?.HumanReview?.Lifecycle.Status);
    }

    [Theory]
    [InlineData("reviewer-role-two", "scope-alpha", "scope-beta")]
    [InlineData("reviewer-role-one", "scope-alpha", "scope-gamma")]
    public async Task Authenticated_but_ineligible_operation_records_one_denial_without_disclosing_its_replay(string roleId, string firstScope, string secondScope)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = roleId, ScopeIds = [firstScope, secondScope] };
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "denied-one", HumanReviewDecisionKind.Reject);

        var first = await service.DecideAsync(command);
        var second = await service.DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, first.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Denied, first.Receipt?.Disposition);
        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, second.Status);
        Assert.Null(second.Receipt);
        Assert.Equal(1, store.UpdateCount);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, store.Run?.HumanReview?.Lifecycle.Status);
    }

    [Fact]
    public async Task Supported_but_unoffered_decision_records_a_denial_without_an_accepted_decision()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync(ImmutableArray.Create(HumanReviewDecisionKind.Approve));
        var store = new HumanReviewDecisionTestStore(fixture.Run);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "unoffered-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Denied, result.Receipt?.Disposition);
        Assert.Equal(1, store.UpdateCount);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, store.Run?.HumanReview?.Lifecycle.Status);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task A_later_denied_authorization_cannot_disclose_a_previously_accepted_replay()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "protected-replay", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification.");

        Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, (await service.DecideAsync(command)).Status);
        authorizer.IsAuthorized = false;
        authorizer.ActorId = null;
        authorizer.ReviewerRoleId = null;
        authorizer.ScopeIds = default;
        authorizer.CorrelationId = null;
        var deniedReplay = await service.DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Denied, deniedReplay.Status);
        Assert.Null(deniedReplay.Receipt);
        Assert.Equal(1, store.UpdateCount);
    }

    [Fact]
    public async Task Inclusive_expiry_appends_many_expired_audits_but_exactly_one_expired_lifecycle()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var expiry = fixture.Request.Timing.ExpiresAtUtc;
        var service = Service(store, new HumanReviewDecisionTestAuthorizer(), expiry, expiry.AddMinutes(1));

        var first = await service.DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "expired-one", HumanReviewDecisionKind.Reject));
        var afterFirst = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var second = await service.DecideAsync(HumanReviewDecisionTestData.Command(afterFirst, "expired-two", HumanReviewDecisionKind.Cancel));

        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, first.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, second.Status);
        var review = Assert.IsType<HumanReviewRunState>(store.Run?.HumanReview);
        Assert.Equal(2, review.OperationReceipts.Length);
        Assert.Equal(2, review.LifecycleHistory.Length);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, review.Lifecycle.Status);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Inclusive_expiry_closes_a_nonterminal_request_even_when_the_caller_predecessor_is_stale()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "expired-stale-one", HumanReviewDecisionKind.Approve, expectedLifecycleVersion: fixture.Run.LifecycleVersion - 1);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Request.Timing.ExpiresAtUtc).DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Expired, result.Receipt?.Disposition);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, store.Run?.HumanReview?.Lifecycle.Status);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Null(store.Run?.HumanReview?.ContinuationReservation);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Authorization_that_completes_at_expiry_records_expiry_without_accepting_approval()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var authorizationAtUtc = fixture.Request.Timing.ExpiresAtUtc.AddTicks(-1);

        var result = await Service(store, authorizer, authorizationAtUtc, fixture.Request.Timing.ExpiresAtUtc).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "expiry-during-authorization", HumanReviewDecisionKind.Approve));

        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, result.Status);
        Assert.Equal(fixture.Request.Timing.ExpiresAtUtc, result.Receipt?.RecordedAtUtc);
        Assert.Equal(authorizationAtUtc, Assert.Single(authorizer.Requests).EvaluatedAtUtc);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Null(store.Run?.HumanReview?.AcceptedTerminalDecision);
        Assert.Null(store.Run?.HumanReview?.ContinuationReservation);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, store.Run?.HumanReview?.Lifecycle.Status);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Approve, HumanReviewLifecycleStatus.Approved, 2)]
    [InlineData(HumanReviewDecisionKind.Reject, HumanReviewLifecycleStatus.Rejected, 1)]
    [InlineData(HumanReviewDecisionKind.Cancel, HumanReviewLifecycleStatus.Cancelled, 1)]
    public async Task Fifteen_information_decisions_reserve_accepted_capacity_for_an_eligible_terminal_decision(HumanReviewDecisionKind kind, HumanReviewLifecycleStatus lifecycle, int terminalEventCount)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var information = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        for (var index = 0; index < HumanReviewContractLimits.MaxAcceptedDecisions - 1; index++)
        {
            var current = Assert.IsType<CustomLoopRunRecord>(store.Run);
            var result = await information.DecideAsync(HumanReviewDecisionTestData.Command(current, $"information-{index:00}", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
            Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, result.Status);
        }

        var beforeTerminal = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var terminalCommand = HumanReviewDecisionTestData.Command(beforeTerminal, "terminal-after-information", kind, "Terminal decision detail.");
        var terminal = await information.DecideAsync(terminalCommand);
        var afterTerminal = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var replay = await information.DecideAsync(terminalCommand);
        var divergent = await information.DecideAsync(terminalCommand with { Detail = "A different terminal decision detail." });

        Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, terminal.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, terminal.Receipt?.Disposition);
        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, replay.Status);
        Assert.Equal(terminal.Receipt?.ReceiptHash, replay.Receipt?.ReceiptHash);
        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, divergent.Status);
        Assert.Null(divergent.Receipt);
        var review = Assert.IsType<HumanReviewRunState>(store.Run?.HumanReview);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, review.AcceptedDecisions.Length);
        Assert.Equal(kind, review.AcceptedTerminalDecision?.Kind);
        Assert.Equal(lifecycle, review.Lifecycle.Status);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, review.OperationReceipts.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions + 1, review.LifecycleHistory.Length);
        Assert.Equal(kind == HumanReviewDecisionKind.Approve, review.ContinuationReservation is not null);
        Assert.Equal(beforeTerminal.Events.Length + terminalEventCount, afterTerminal.Events.Length);
        Assert.Equal(afterTerminal.Events.Length, store.Run?.Events.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, store.UpdateCount);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Sixteenth_information_decision_is_rejected_while_the_reserved_slot_keeps_later_expiry_and_exact_replay_recordable()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var information = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        for (var index = 0; index < HumanReviewContractLimits.MaxAcceptedDecisions - 1; index++)
        {
            var current = Assert.IsType<CustomLoopRunRecord>(store.Run);
            var result = await information.DecideAsync(HumanReviewDecisionTestData.Command(current, $"information-{index:00}", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
            Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, result.Status);
        }

        var beforeLimit = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var sixteenth = await information.DecideAsync(HumanReviewDecisionTestData.Command(beforeLimit, "information-sixteenth", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
        var expiryCommand = HumanReviewDecisionTestData.Command(beforeLimit, "expiry-after-information", HumanReviewDecisionKind.Reject);
        var expiry = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Request.Timing.ExpiresAtUtc).DecideAsync(expiryCommand);
        var afterExpiry = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var expiryService = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Request.Timing.ExpiresAtUtc);
        var replay = await expiryService.DecideAsync(expiryCommand);
        var divergent = await expiryService.DecideAsync(expiryCommand with { Kind = HumanReviewDecisionKind.Cancel });

        Assert.Equal(HumanReviewDecisionServiceStatus.LimitExceeded, sixteenth.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, expiry.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Expired, expiry.Receipt?.Disposition);
        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, replay.Status);
        Assert.Equal(expiry.Receipt?.ReceiptHash, replay.Receipt?.ReceiptHash);
        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, divergent.Status);
        Assert.Null(divergent.Receipt);
        var review = Assert.IsType<HumanReviewRunState>(store.Run?.HumanReview);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions - 1, review.AcceptedDecisions.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, review.OperationReceipts.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions + 1, review.LifecycleHistory.Length);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, review.Lifecycle.Status);
        Assert.Equal(beforeLimit.Events.Length + 1, afterExpiry.Events.Length);
        Assert.Equal(afterExpiry.Events.Length, store.Run?.Events.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, store.UpdateCount);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Rfi_only_request_accepts_sixteen_information_decisions_then_expiry_and_replay_without_a_terminal_capacity_reservation()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync([HumanReviewDecisionKind.RequestInformation]);
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var information = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        for (var index = 0; index < HumanReviewContractLimits.MaxAcceptedDecisions; index++)
        {
            var current = Assert.IsType<CustomLoopRunRecord>(store.Run);
            var result = await information.DecideAsync(HumanReviewDecisionTestData.Command(current, $"rfi-only-{index:00}", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
            Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, result.Status);
        }

        var beforeLimit = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var seventeenth = await information.DecideAsync(HumanReviewDecisionTestData.Command(beforeLimit, "rfi-only-seventeenth", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
        var expiryCommand = HumanReviewDecisionTestData.Command(beforeLimit, "rfi-only-expiry", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification.");
        var expiry = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Request.Timing.ExpiresAtUtc).DecideAsync(expiryCommand);
        var afterExpiry = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var expiryService = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Request.Timing.ExpiresAtUtc);
        var replay = await expiryService.DecideAsync(expiryCommand);
        var divergent = await expiryService.DecideAsync(expiryCommand with { Detail = "A different redacted clarification." });

        Assert.Equal(HumanReviewDecisionServiceStatus.LimitExceeded, seventeenth.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Expired, expiry.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Expired, expiry.Receipt?.Disposition);
        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, replay.Status);
        Assert.Equal(expiry.Receipt?.ReceiptHash, replay.Receipt?.ReceiptHash);
        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, divergent.Status);
        Assert.Null(divergent.Receipt);
        var review = Assert.IsType<HumanReviewRunState>(store.Run?.HumanReview);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions, review.AcceptedDecisions.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions + 1, review.OperationReceipts.Length);
        Assert.Equal(HumanReviewContractLimits.MaxLifecycleHistory, review.LifecycleHistory.Length);
        Assert.Equal(HumanReviewLifecycleStatus.Expired, review.Lifecycle.Status);
        Assert.Null(review.AcceptedTerminalDecision);
        Assert.Null(review.ContinuationReservation);
        Assert.Equal(beforeLimit.Events.Length + 1, afterExpiry.Events.Length);
        Assert.Equal(afterExpiry.Events.Length, store.Run?.Events.Length);
        Assert.Equal(HumanReviewContractLimits.MaxAcceptedDecisions + 1, store.UpdateCount);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Terminal_approval_preserves_its_reservation_and_appends_only_a_conflict_audit_for_a_loser()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var service = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2));

        await service.DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "winner-one", HumanReviewDecisionKind.Approve));
        var approved = Assert.IsType<CustomLoopRunRecord>(store.Run);
        var reservationHash = approved.HumanReview?.ContinuationReservation?.ReservationHash;
        var lifecycleCount = approved.HumanReview?.LifecycleHistory.Length;
        var loser = await service.DecideAsync(HumanReviewDecisionTestData.Command(approved, "loser-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, loser.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, loser.Receipt?.Disposition);
        var persisted = Assert.IsType<CustomLoopRunRecord>(store.Run);
        Assert.Equal(reservationHash, persisted.HumanReview?.ContinuationReservation?.ReservationHash);
        Assert.Equal(lifecycleCount, persisted.HumanReview?.LifecycleHistory.Length);
        Assert.Equal(2, persisted.HumanReview?.OperationReceipts.Length);
        Assert.True(CustomLoopRunValidator.Validate(persisted).IsValid);
    }

    [Fact]
    public async Task Exact_replay_survives_a_stale_expected_version_but_divergent_operation_reuse_never_appends()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var service = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "replay-one", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification.");

        var first = await service.DecideAsync(command);
        var replay = await service.DecideAsync(command);
        var divergent = await service.DecideAsync(command with { Detail = "A different redacted clarification." });

        Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, first.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, replay.Status);
        Assert.Equal(first.Receipt?.ReceiptHash, replay.Receipt?.ReceiptHash);
        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, divergent.Status);
        Assert.Null(divergent.Receipt);
        Assert.Equal(1, store.UpdateCount);
    }

    [Fact]
    public async Task New_operation_against_a_stale_predecessor_appends_one_conflict_when_the_review_boundary_remains_valid()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "stale-one", HumanReviewDecisionKind.Reject, expectedLifecycleVersion: fixture.Run.LifecycleVersion - 1);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, result.Receipt?.Disposition);
        Assert.Equal(1, store.UpdateCount);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(HumanReviewLifecycleStatus.Pending, store.Run?.HumanReview?.Lifecycle.Status);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Stale_predecessor_remains_a_conflict_after_a_precommit_cas_retry()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run) { VersionConflictsRemaining = 1 };
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "stale-retry-one", HumanReviewDecisionKind.Approve, expectedLifecycleVersion: fixture.Run.LifecycleVersion - 1);

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2)).DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, result.Receipt?.Disposition);
        Assert.Equal(2, store.UpdateAttempts);
        Assert.Equal(1, store.UpdateCount);
        Assert.Empty(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Null(store.Run?.HumanReview?.ContinuationReservation);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("operation")]
    [InlineData("proposal")]
    [InlineData("time")]
    public async Task Mismatched_authorization_echo_fails_closed_without_mutation(string mutation)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer
        {
            Handler = (request, _) => Task.FromResult<HumanReviewDecisionAuthorization?>(mutation switch
            {
                "request" => Bound(request) with { RequestHash = new string('f', HumanReviewContractLimits.Sha256HexCharacters) },
                "operation" => Bound(request) with { DecisionOperationId = "other-operation" },
                "proposal" => Bound(request) with { ProposalHash = new string('e', HumanReviewContractLimits.Sha256HexCharacters) },
                _ => Bound(request) with { EvaluatedAtUtc = request.EvaluatedAtUtc.AddTicks(1) },
            })
        };

        var result = await Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "echo-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Receipt);
        Assert.Equal(0, store.UpdateCount);
    }

    [Fact]
    public async Task Malformed_authorized_scope_order_fails_closed_without_recording_a_denial()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var authorizer = new HumanReviewDecisionTestAuthorizer { ScopeIds = ["scope-beta", "scope-alpha"] };

        var result = await Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "scope-order-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, result.Status);
        Assert.Null(result.Receipt);
        Assert.Equal(0, store.UpdateCount);
    }

    [Fact]
    public async Task Unavailable_null_and_cancelled_authorizer_paths_do_not_mutate_and_cancellation_propagates()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "port-one", HumanReviewDecisionKind.Reject);
        var unavailableStore = new HumanReviewDecisionTestStore(fixture.Run);
        var unavailable = new HumanReviewDecisionTestAuthorizer { Handler = (_, _) => throw new IOException("authorizer unavailable") };
        var nullStore = new HumanReviewDecisionTestStore(fixture.Run);
        var nullResult = new HumanReviewDecisionTestAuthorizer { Handler = (_, _) => Task.FromResult<HumanReviewDecisionAuthorization?>(null) };
        var nonCallerCancellationStore = new HumanReviewDecisionTestStore(fixture.Run);
        var nonCallerCancellation = new HumanReviewDecisionTestAuthorizer { Handler = (_, _) => throw new OperationCanceledException("The dependency cancelled independently.") };
        var cancelledStore = new HumanReviewDecisionTestStore(fixture.Run);
        var cancelled = new HumanReviewDecisionTestAuthorizer { Handler = (_, token) => Task.FromCanceled<HumanReviewDecisionAuthorization?>(token) };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await Service(unavailableStore, unavailable, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await Service(nullStore, nullResult, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await Service(nonCallerCancellationStore, nonCallerCancellation, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(command)).Status);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(cancelledStore, cancelled, fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(command, cancellation.Token));
        Assert.Equal(0, unavailableStore.UpdateCount);
        Assert.Equal(0, nullStore.UpdateCount);
        Assert.Equal(0, nonCallerCancellationStore.UpdateCount);
        Assert.Equal(0, cancelledStore.UpdateCount);
    }

    [Fact]
    public async Task Untrusted_time_and_malformed_or_unavailable_store_results_fail_closed_without_mutation()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "store-port-one", HumanReviewDecisionKind.Reject);
        var clockStore = new HumanReviewDecisionTestStore(fixture.Run);
        var readStore = new HumanReviewDecisionTestStore(fixture.Run) { GetOverrideAsync = (_, _) => throw new IOException("store unavailable") };
        var cancelledReadStore = new HumanReviewDecisionTestStore(fixture.Run) { GetOverrideAsync = (_, _) => throw new OperationCanceledException("The store cancelled independently.") };
        var responseStore = new HumanReviewDecisionTestStore(fixture.Run)
        {
            UpdateOverrideAsync = (_, _, _) => Task.FromResult(new CustomLoopRunStoreResult(CustomLoopRunStoreStatus.Updated, null, null))
        };
        var throwingUpdateStore = new HumanReviewDecisionTestStore(fixture.Run)
        {
            UpdateOverrideAsync = (_, _, _) => throw new IOException("store update unavailable")
        };
        var cancelledUpdateStore = new HumanReviewDecisionTestStore(fixture.Run)
        {
            UpdateOverrideAsync = (_, _, _) => throw new OperationCanceledException("The store update cancelled independently.")
        };

        var untrustedClock = new HumanReviewDecisionService(clockStore, new HumanReviewDecisionTestAuthorizer(), new HumanReviewDecisionTestClock(default(DateTimeOffset)));
        var unavailableRead = Service(readStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var cancelledRead = Service(cancelledReadStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var malformedResponse = Service(responseStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var unavailableUpdate = Service(throwingUpdateStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var cancelledUpdate = Service(cancelledUpdateStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await untrustedClock.DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await unavailableRead.DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await cancelledRead.DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await malformedResponse.DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await unavailableUpdate.DecideAsync(command)).Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, (await cancelledUpdate.DecideAsync(command)).Status);
        Assert.Equal(0, clockStore.UpdateCount);
        Assert.Equal(0, readStore.UpdateCount);
        Assert.Equal(0, cancelledReadStore.UpdateCount);
        Assert.Equal(0, responseStore.UpdateCount);
        Assert.Equal(0, throwingUpdateStore.UpdateCount);
        Assert.Equal(0, cancelledUpdateStore.UpdateCount);
        Assert.Equal(3, throwingUpdateStore.UpdateAttempts);
        Assert.Equal(3, cancelledUpdateStore.UpdateAttempts);
    }

    [Fact]
    public async Task Non_utc_backward_and_throwing_trusted_clocks_fail_closed_before_authorization()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "clock-one", HumanReviewDecisionKind.Reject);
        var offsetStore = new HumanReviewDecisionTestStore(fixture.Run);
        var backwardStore = new HumanReviewDecisionTestStore(fixture.Run);
        var throwingStore = new HumanReviewDecisionTestStore(fixture.Run);
        var offsetAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var backwardAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var throwingAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var nonUtc = new DateTimeOffset(fixture.Run.UpdatedAtUtc.AddMinutes(1).DateTime, TimeSpan.FromHours(1));

        var offset = await new HumanReviewDecisionService(offsetStore, offsetAuthorizer, new HumanReviewDecisionTestClock(nonUtc)).DecideAsync(command);
        var backward = await new HumanReviewDecisionService(backwardStore, backwardAuthorizer, new HumanReviewDecisionTestClock(fixture.Run.UpdatedAtUtc.AddTicks(-1))).DecideAsync(command);
        var throwing = await new HumanReviewDecisionService(throwingStore, throwingAuthorizer, new HumanReviewDecisionTestClock()).DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, offset.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, backward.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, throwing.Status);
        Assert.Empty(offsetAuthorizer.Requests);
        Assert.Empty(backwardAuthorizer.Requests);
        Assert.Empty(throwingAuthorizer.Requests);
        Assert.Equal(0, offsetStore.UpdateCount);
        Assert.Equal(0, backwardStore.UpdateCount);
        Assert.Equal(0, throwingStore.UpdateCount);
    }

    [Fact]
    public async Task Invalid_or_backward_trusted_time_after_authorization_fails_closed_without_mutation()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "post-authorization-clock-one", HumanReviewDecisionKind.Reject);
        var authorizationAtUtc = fixture.Run.UpdatedAtUtc.AddMinutes(2);
        var nonUtc = new DateTimeOffset(authorizationAtUtc.AddMinutes(1).DateTime, TimeSpan.FromHours(1));
        var defaultStore = new HumanReviewDecisionTestStore(fixture.Run);
        var backwardStore = new HumanReviewDecisionTestStore(fixture.Run);
        var offsetStore = new HumanReviewDecisionTestStore(fixture.Run);
        var defaultAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var backwardAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var offsetAuthorizer = new HumanReviewDecisionTestAuthorizer();

        var invalid = await new HumanReviewDecisionService(defaultStore, defaultAuthorizer, new HumanReviewDecisionTestClock(authorizationAtUtc, default)).DecideAsync(command);
        var backward = await new HumanReviewDecisionService(backwardStore, backwardAuthorizer, new HumanReviewDecisionTestClock(authorizationAtUtc, authorizationAtUtc.AddTicks(-1))).DecideAsync(command);
        var offset = await new HumanReviewDecisionService(offsetStore, offsetAuthorizer, new HumanReviewDecisionTestClock(authorizationAtUtc, nonUtc)).DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, invalid.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, backward.Status);
        Assert.Equal(HumanReviewDecisionServiceStatus.Unavailable, offset.Status);
        Assert.Single(defaultAuthorizer.Requests);
        Assert.Single(backwardAuthorizer.Requests);
        Assert.Single(offsetAuthorizer.Requests);
        Assert.Equal(0, defaultStore.UpdateCount);
        Assert.Equal(0, backwardStore.UpdateCount);
        Assert.Equal(0, offsetStore.UpdateCount);
    }

    [Fact]
    public async Task Cancellation_after_a_durable_write_reconciles_the_exact_operation_without_duplication()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run) { PersistThenCancelOnce = true };

        var result = await Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "cancelled-response-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, result.Receipt?.Disposition);
        Assert.Equal(1, store.UpdateCount);
        Assert.Single(store.Run?.HumanReview?.OperationReceipts ?? []);
        Assert.Single(store.Run?.HumanReview?.AcceptedDecisions ?? []);
    }

    [Fact]
    public async Task Persisted_response_loss_rereads_reauthorizes_and_returns_the_exact_replay()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run) { PersistThenThrowOnce = true };
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2));

        var result = await service.DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "loss-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, result.Status);
        Assert.Equal(1, store.UpdateCount);
        Assert.Equal(2, authorizer.Requests.Count);
        Assert.Equal(2, store.ReadCount);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, result.Receipt?.Disposition);
    }

    [Fact]
    public async Task Final_attempt_response_loss_performs_an_exact_durable_readback_before_returning_unavailable()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run) { VersionConflictsRemaining = 2, PersistThenThrowOnce = true };
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var service = Service(store, authorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2), fixture.Run.UpdatedAtUtc.AddMinutes(3));

        var result = await service.DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "final-loss-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Accepted, result.Receipt?.Disposition);
        Assert.Equal(3, store.UpdateAttempts);
        Assert.Equal(1, store.UpdateCount);
        Assert.Equal(4, store.ReadCount);
        Assert.Equal(3, authorizer.Requests.Count);
        Assert.Single(store.Run?.HumanReview?.OperationReceipts ?? []);
    }

    [Fact]
    public async Task Same_operation_cas_race_returns_replay_after_the_winner_is_reread()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var winner = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var loserAuthorizer = new HumanReviewDecisionTestAuthorizer();
        var loser = Service(store, loserAuthorizer, fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2));
        var command = HumanReviewDecisionTestData.Command(fixture.Run, "same-race", HumanReviewDecisionKind.Reject);
        store.BeforeFirstUpdateAsync = async (_, _, token) => Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, (await winner.DecideAsync(command, token)).Status);

        var result = await loser.DecideAsync(command);

        Assert.Equal(HumanReviewDecisionServiceStatus.Replayed, result.Status);
        Assert.Equal(1, store.UpdateCount);
        Assert.Equal(2, loserAuthorizer.Requests.Count);
    }

    [Fact]
    public async Task Different_operation_cas_loser_appends_one_conflict_after_a_terminal_winner()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var winner = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var loser = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2));
        var winnerCommand = HumanReviewDecisionTestData.Command(fixture.Run, "race-winner", HumanReviewDecisionKind.Approve);
        var loserCommand = HumanReviewDecisionTestData.Command(fixture.Run, "race-loser", HumanReviewDecisionKind.Reject);
        store.BeforeFirstUpdateAsync = async (_, _, token) => Assert.Equal(HumanReviewDecisionServiceStatus.Accepted, (await winner.DecideAsync(winnerCommand, token)).Status);

        var result = await loser.DecideAsync(loserCommand);

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, result.Receipt?.Disposition);
        Assert.Equal(2, store.UpdateCount);
        Assert.Equal(HumanReviewDecisionKind.Approve, store.Run?.HumanReview?.AcceptedTerminalDecision?.Kind);
        Assert.Equal(2, store.Run?.HumanReview?.OperationReceipts.Length);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Different_operation_cas_loser_appends_conflict_after_an_information_request_winner()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run);
        var winner = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1));
        var loser = Service(store, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1), fixture.Run.UpdatedAtUtc.AddMinutes(2));
        var winnerCommand = HumanReviewDecisionTestData.Command(fixture.Run, "information-race-winner", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification.");
        var loserCommand = HumanReviewDecisionTestData.Command(fixture.Run, "approval-race-loser", HumanReviewDecisionKind.Approve);
        store.BeforeFirstUpdateAsync = async (_, _, token) => Assert.Equal(HumanReviewDecisionServiceStatus.InformationRequested, (await winner.DecideAsync(winnerCommand, token)).Status);

        var result = await loser.DecideAsync(loserCommand);

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, result.Status);
        Assert.Equal(HumanReviewDecisionOperationDisposition.Conflict, result.Receipt?.Disposition);
        Assert.Equal(2, store.UpdateCount);
        Assert.Single(store.Run?.HumanReview?.AcceptedDecisions ?? []);
        Assert.Equal(HumanReviewDecisionKind.RequestInformation, store.Run?.HumanReview?.AcceptedDecisions[0].Kind);
        Assert.Null(store.Run?.HumanReview?.ContinuationReservation);
        Assert.Equal(2, store.Run?.HumanReview?.OperationReceipts.Length);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(store.Run)).IsValid);
    }

    [Fact]
    public async Task Retries_are_bounded_to_three_fresh_authorized_cas_attempts()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var store = new HumanReviewDecisionTestStore(fixture.Run)
        {
            UpdateOverrideAsync = (candidate, expected, _) => Task.FromResult(CustomLoopRunStoreResult.VersionConflict(candidate, expected))
        };
        var authorizer = new HumanReviewDecisionTestAuthorizer();
        var firstAttemptUtc = fixture.Run.UpdatedAtUtc.AddMinutes(1);
        var secondAttemptUtc = fixture.Run.UpdatedAtUtc.AddMinutes(2);
        var thirdAttemptUtc = fixture.Run.UpdatedAtUtc.AddMinutes(3);
        var clock = new HumanReviewDecisionTestClock(firstAttemptUtc, firstAttemptUtc, secondAttemptUtc, secondAttemptUtc, thirdAttemptUtc, thirdAttemptUtc);

        var result = await new HumanReviewDecisionService(store, authorizer, clock).DecideAsync(HumanReviewDecisionTestData.Command(fixture.Run, "retry-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Conflict, result.Status);
        Assert.Equal(3, store.UpdateAttempts);
        Assert.Equal(3, store.ReadCount);
        Assert.Equal(3, authorizer.Requests.Count);
        Assert.Equal(6, clock.ReadCount);
        Assert.Equal([firstAttemptUtc, secondAttemptUtc, thirdAttemptUtc], authorizer.Requests.Select(request => request.EvaluatedAtUtc));
        Assert.Equal(0, store.UpdateCount);
    }

    [Theory]
    [InlineData(HumanReviewDecisionKind.Approve, false, HumanReviewDecisionServiceStatus.Accepted, HumanReviewLifecycleStatus.Approved)]
    [InlineData(HumanReviewDecisionKind.Reject, false, HumanReviewDecisionServiceStatus.Accepted, HumanReviewLifecycleStatus.Rejected)]
    [InlineData(HumanReviewDecisionKind.Cancel, false, HumanReviewDecisionServiceStatus.Accepted, HumanReviewLifecycleStatus.Cancelled)]
    [InlineData(HumanReviewDecisionKind.Reject, true, HumanReviewDecisionServiceStatus.Expired, HumanReviewLifecycleStatus.Expired)]
    public async Task Denial_receipt_quota_reserves_capacity_for_an_eligible_terminal_or_expiry_outcome(HumanReviewDecisionKind kind, bool atExpiry, HumanReviewDecisionServiceStatus expectedStatus, HumanReviewLifecycleStatus expectedLifecycle)
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var quotaStore = new HumanReviewDecisionTestStore(fixture.Run);
        var denied = new HumanReviewDecisionTestAuthorizer { ReviewerRoleId = "reviewer-role-two" };
        var atUtc = atExpiry ? fixture.Request.Timing.ExpiresAtUtc : fixture.Run.UpdatedAtUtc.AddMinutes(1);
        var service = Service(quotaStore, denied, atUtc);
        for (var index = 0; index < HumanReviewContractLimits.MaxDecisionOperationReceipts - 1; index++)
        {
            var current = Assert.IsType<CustomLoopRunRecord>(quotaStore.Run);
            var result = await service.DecideAsync(HumanReviewDecisionTestData.Command(current, $"quota-{index:00}", HumanReviewDecisionKind.Reject));
            Assert.Equal(HumanReviewDecisionServiceStatus.Denied, result.Status);
        }

        var exhausted = await service.DecideAsync(HumanReviewDecisionTestData.Command(Assert.IsType<CustomLoopRunRecord>(quotaStore.Run), "quota-over", HumanReviewDecisionKind.Reject));
        Assert.Equal(HumanReviewDecisionServiceStatus.LimitExceeded, exhausted.Status);
        Assert.Equal(HumanReviewContractLimits.MaxDecisionOperationReceipts - 1, quotaStore.UpdateCount);

        denied.ReviewerRoleId = "reviewer-role-one";
        var beforeTerminal = Assert.IsType<CustomLoopRunRecord>(quotaStore.Run);
        if (!atExpiry)
        {
            var information = await service.DecideAsync(HumanReviewDecisionTestData.Command(beforeTerminal, "quota-information", HumanReviewDecisionKind.RequestInformation, "Need a redacted clarification."));
            var stale = await service.DecideAsync(HumanReviewDecisionTestData.Command(beforeTerminal, "quota-stale", HumanReviewDecisionKind.Reject, expectedLifecycleVersion: beforeTerminal.LifecycleVersion - 1));
            Assert.Equal(HumanReviewDecisionServiceStatus.LimitExceeded, information.Status);
            Assert.Equal(HumanReviewDecisionServiceStatus.LimitExceeded, stale.Status);
        }

        var terminal = await service.DecideAsync(HumanReviewDecisionTestData.Command(beforeTerminal, "quota-terminal", kind));

        Assert.Equal(expectedStatus, terminal.Status);
        Assert.Equal(expectedLifecycle, quotaStore.Run?.HumanReview?.Lifecycle.Status);
        Assert.Equal(HumanReviewContractLimits.MaxDecisionOperationReceipts, quotaStore.Run?.HumanReview?.OperationReceipts.Length);
        Assert.Equal(HumanReviewContractLimits.MaxDecisionOperationReceipts, quotaStore.UpdateCount);
        Assert.Equal(kind == HumanReviewDecisionKind.Approve, quotaStore.Run?.HumanReview?.ContinuationReservation is not null);
        Assert.True(CustomLoopRunValidator.Validate(Assert.IsType<CustomLoopRunRecord>(quotaStore.Run)).IsValid);
    }

    [Fact]
    public async Task Invalid_durable_state_fails_before_the_store_mutation_boundary()
    {
        var fixture = await HumanReviewDecisionTestData.CreateAsync();
        var invalidRun = fixture.Run with { HumanReview = fixture.Run.HumanReview! with { Evidence = default } };
        var invalidStore = new HumanReviewDecisionTestStore(invalidRun);
        var invalid = await Service(invalidStore, new HumanReviewDecisionTestAuthorizer(), fixture.Run.UpdatedAtUtc.AddMinutes(1)).DecideAsync(HumanReviewDecisionTestData.Command(invalidRun, "invalid-one", HumanReviewDecisionKind.Reject));

        Assert.Equal(HumanReviewDecisionServiceStatus.Invalid, invalid.Status);
        Assert.Equal(0, invalidStore.UpdateCount);
    }

    private static HumanReviewDecisionService Service(HumanReviewDecisionTestStore store, HumanReviewDecisionTestAuthorizer authorizer, params DateTimeOffset[] times)
        => new(store, authorizer, new HumanReviewDecisionTestClock(times));

    private static HumanReviewDecisionAuthorization Bound(HumanReviewDecisionAuthorizationRequest request)
        => new(true, request.RequestHash, request.DecisionOperationId, request.ProposalHash, request.EvaluatedAtUtc, "reviewer-user", "reviewer-role-one", ImmutableArray.Create("scope-alpha", "scope-beta"), "authorization-one");
}
