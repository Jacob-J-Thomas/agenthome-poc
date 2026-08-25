using EmbodySense.Core.Common.HumanReview;
using EmbodySense.Core.Common.HumanReview.Models;

namespace EmbodySense.Core.Common.Tests.HumanReview;

public sealed class HumanReviewDecisionReceiptContractTests
{
    [Fact]
    public void Canonical_proposal_receipt_and_approval_reservation_bind_the_exact_request_and_decision()
    {
        var request = HumanReviewTestData.Request();
        var decision = HumanReviewTestData.Decision(request);
        var proposal = HumanReviewContractHash.ApplyDecisionProposal(new HumanReviewDecisionProposal(1, decision.DecisionOperationId, decision.Kind, null, string.Empty));
        var receipt = HumanReviewContractHash.ApplyDecisionOperationReceipt(new HumanReviewDecisionOperationReceipt(
            1, decision.DecisionOperationId, proposal.ProposalHash, new HumanReviewRequestReference(request.RequestId, request.RequestHash), HumanReviewDecisionOperationDisposition.Accepted,
            new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash), HumanReviewTestData.CreatedAtUtc.AddMinutes(2),
            new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "server-one", "receipt-correlation-one", HumanReviewTestData.CreatedAtUtc.AddMinutes(2), string.Empty), string.Empty));
        var reservation = HumanReviewContractHash.ApplyContinuationReservation(new HumanReviewContinuationReservation(
            1, "review-reservation-one", new HumanReviewRequestReference(request.RequestId, request.RequestHash), receipt.Decision!, HumanReviewTestData.CreatedAtUtc.AddMinutes(3),
            new HumanReviewProvenance(HumanReviewProvenanceKind.Coordinator, "coordinator-one", "reservation-correlation-one", HumanReviewTestData.CreatedAtUtc.AddMinutes(3), string.Empty), string.Empty));

        Assert.True(HumanReviewContractValidator.ValidateDecisionProposal(proposal).IsValid);
        var privateProposal = HumanReviewContractHash.ApplyDecisionProposal(proposal with { Detail = "A private reviewer detail.", ProposalHash = string.Empty });
        Assert.Contains("[REDACTED]", privateProposal.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("A private reviewer detail.", privateProposal.ToString(), StringComparison.Ordinal);
        Assert.True(HumanReviewContractValidator.ValidateDecisionOperationReceipt(request, receipt).IsValid);
        Assert.True(HumanReviewContractValidator.ValidateContinuationReservation(request, reservation).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateDecisionOperationReceipt(request, HumanReviewContractHash.ApplyDecisionOperationReceipt(receipt with { Decision = null, ReceiptHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateContinuationReservation(request, HumanReviewContractHash.ApplyContinuationReservation(reservation with { Decision = reservation.Decision with { Kind = HumanReviewDecisionKind.Reject }, ReservationHash = string.Empty })).IsValid);
        Assert.False(HumanReviewContractValidator.ValidateDecisionProposal(proposal with { ProposalHash = HumanReviewTestData.Hash('b') }).IsValid);
    }

    [Theory]
    [InlineData(HumanReviewDecisionOperationDisposition.Denied)]
    [InlineData(HumanReviewDecisionOperationDisposition.Conflict)]
    [InlineData(HumanReviewDecisionOperationDisposition.Expired)]
    public void Nonaccepted_receipts_require_no_decision(HumanReviewDecisionOperationDisposition disposition)
    {
        var request = HumanReviewTestData.Request();
        var receipt = HumanReviewContractHash.ApplyDecisionOperationReceipt(new HumanReviewDecisionOperationReceipt(
            1, "operation-" + disposition.ToString().ToLowerInvariant(), HumanReviewTestData.Hash('a'), new HumanReviewRequestReference(request.RequestId, request.RequestHash), disposition, null,
            HumanReviewTestData.CreatedAtUtc.AddMinutes(2), new HumanReviewProvenance(HumanReviewProvenanceKind.Server, "server-one", "receipt-correlation-two", HumanReviewTestData.CreatedAtUtc.AddMinutes(2), string.Empty), string.Empty));

        Assert.True(HumanReviewContractValidator.ValidateDecisionOperationReceipt(request, receipt).IsValid);
        var decision = HumanReviewTestData.Decision(request, HumanReviewDecisionKind.Reject);
        Assert.False(HumanReviewContractValidator.ValidateDecisionOperationReceipt(request, HumanReviewContractHash.ApplyDecisionOperationReceipt(receipt with
        {
            Decision = new HumanReviewDecisionReference(decision.DecisionId, decision.DecisionOperationId, decision.Kind, decision.DecisionHash),
            ReceiptHash = string.Empty
        })).IsValid);
    }
}
