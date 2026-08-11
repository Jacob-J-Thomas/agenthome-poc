using EmbodySense.Core.Common.HumanInput.Lifecycle;
using EmbodySense.Core.Common.HumanInput.Lifecycle.Models;

namespace EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

public sealed class HumanInputRequestLifecycleHeadTests
{
    [Fact]
    public void Pending_and_supersession_chain_heads_validate()
    {
        var request = HumanInputLifecycleTestData.Request();
        var pending = HumanInputLifecycleTestData.Head(request);
        var superseded = pending with
        {
            Status = HumanInputRequestLifecycleStatus.Superseded,
            SupersedesRequestId = "request-zero",
            SupersededByRequestId = "request-two"
        };

        Assert.True(HumanInputRequestLifecycleValidator.ValidateHead(pending).IsValid);
        Assert.True(HumanInputRequestLifecycleValidator.ValidateHead(superseded).IsValid);
    }

    [Fact]
    public void Head_validator_closes_every_identity_version_status_count_lineage_and_time_boundary()
    {
        var valid = HumanInputLifecycleTestData.Head(HumanInputLifecycleTestData.Request());
        var variants = new[]
        {
            valid with { SchemaVersion = 2 },
            valid with { RequestId = "Invalid" },
            valid with { LifecycleVersion = 0 },
            valid with { LifecycleVersion = HumanInputRequestLifecycleContractLimits.MaxLifecycleVersion + 1 },
            valid with { Status = HumanInputRequestLifecycleStatus.Unknown },
            valid with { Status = (HumanInputRequestLifecycleStatus)99 },
            valid with { CurrentRequest = valid.CurrentRequest with { RequestId = "request-other" } },
            valid with { ReminderCount = -1 },
            valid with { ReminderCount = HumanInputRequestLifecycleContractLimits.MaxReminderCount + 1 },
            valid with { SupersedesRequestId = valid.RequestId },
            valid with { SupersededByRequestId = "request-two" },
            valid with { LastOperationId = "Invalid" },
            valid with { UpdatedAtUtc = default },
            valid with { UpdatedAtUtc = valid.UpdatedAtUtc.ToOffset(TimeSpan.FromHours(1)) }
        };

        Assert.All(variants, variant => Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(variant).IsValid));
        Assert.False(HumanInputRequestLifecycleValidator.ValidateHead(null).IsValid);
    }

    [Fact]
    public void Validation_result_snapshots_and_bounds_the_supplied_error_sequence()
    {
        var source = Enumerable.Range(0, HumanInputRequestLifecycleContractLimits.MaxValidationErrors + 5)
            .Select(_ => new HumanInputRequestLifecycleValidationError(HumanInputRequestLifecycleValidationErrorCode.InvalidHeadShape, "$", "Value-free error."))
            .ToList();
        var result = new HumanInputRequestLifecycleValidationResult(source);
        source.Clear();

        Assert.Equal(HumanInputRequestLifecycleContractLimits.MaxValidationErrors, result.Errors.Count);
        Assert.False(result.IsValid);
        Assert.Throws<ArgumentNullException>(() => new HumanInputRequestLifecycleValidationResult(null!));
    }

    [Fact]
    public void Every_closed_lifecycle_status_except_unknown_is_structurally_supported()
    {
        var request = HumanInputLifecycleTestData.Request();
        foreach (var status in Enum.GetValues<HumanInputRequestLifecycleStatus>().Where(value => value != HumanInputRequestLifecycleStatus.Unknown))
        {
            var head = HumanInputLifecycleTestData.Head(
                request,
                status: status,
                supersededByRequestId: status == HumanInputRequestLifecycleStatus.Superseded ? "request-two" : null);
            Assert.True(HumanInputRequestLifecycleValidator.ValidateHead(head).IsValid, status.ToString());
        }
    }
}
