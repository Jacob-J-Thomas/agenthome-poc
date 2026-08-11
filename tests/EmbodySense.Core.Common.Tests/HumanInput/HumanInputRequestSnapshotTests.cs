using EmbodySense.Core.Common.HumanInput;
using EmbodySense.Core.Common.HumanInput.Models;
using EmbodySense.Core.Common.Tests.HumanInput.Lifecycle;

namespace EmbodySense.Core.Common.Tests.HumanInput;

public sealed class HumanInputRequestSnapshotTests
{
    [Fact]
    public void Capture_deep_copies_every_mutable_request_collection()
    {
        var request = HumanInputLifecycleTestData.StructuredRequest();

        Assert.True(HumanInputRequestSnapshot.TryCapture(request, out var snapshot, out var validation));
        Assert.True(validation.IsValid);

        request.EligibleRespondents[0] = new HumanInputEligibleRespondent("attacker", "hostile-route");
        request.ResponseSchema.StructuredFields![0] = new HumanInputStructuredFieldSchema("hostile", HumanInputStructuredFieldKind.Text, false, 1, null);
        request.ResponseSchema.StructuredFields[1].Choices![0] = new HumanInputChoice("hostile", "Hostile value");

        Assert.Equal("user-one", snapshot!.EligibleRespondents[0].RespondentId);
        Assert.Equal("field-one", snapshot.ResponseSchema.StructuredFields![0].FieldId);
        Assert.Equal("choice-one", snapshot.ResponseSchema.StructuredFields[1].Choices![0].ChoiceId);
        Assert.True(HumanInputValidator.ValidateRequest(snapshot).IsValid);
        Assert.NotSame(request.Binding, snapshot.Binding);
        Assert.NotSame(request.Timing, snapshot.Timing);
        Assert.NotSame(request.ResponsePolicy, snapshot.ResponsePolicy);
        Assert.NotSame(request.ContinuationBinding, snapshot.ContinuationBinding);
    }

    [Fact]
    public void Capture_fails_closed_for_null_invalid_and_over_bound_shapes()
    {
        Assert.False(HumanInputRequestSnapshot.TryCapture(null, out var missing, out var missingValidation));
        Assert.Null(missing);
        Assert.Contains(missingValidation.Errors, error => error.Code == "request_required");

        var invalid = HumanInputLifecycleTestData.Request() with { RequestHash = new string('a', 64) };
        Assert.False(HumanInputRequestSnapshot.TryCapture(invalid, out var malformed, out var invalidValidation));
        Assert.Null(malformed);
        Assert.Contains(invalidValidation.Errors, error => error.Code == "request_hash_mismatch");

        var oversized = HumanInputLifecycleTestData.Request() with
        {
            EligibleRespondents = Enumerable.Range(0, HumanInputLimits.MaxEligibleRespondents + 1)
                .Select(index => new HumanInputEligibleRespondent($"user-{index}", $"route-{index}"))
                .ToArray()
        };
        Assert.False(HumanInputRequestSnapshot.TryCapture(oversized, out var rejected, out var oversizedValidation));
        Assert.Null(rejected);
        Assert.Contains(oversizedValidation.Errors, error => error.Code == "request_snapshot_unbounded");
    }

    [Fact]
    public void Capture_rejects_over_bound_nested_choice_arrays_before_hashing()
    {
        var request = HumanInputLifecycleTestData.StructuredRequest();
        request.ResponseSchema.StructuredFields![1] = request.ResponseSchema.StructuredFields[1] with
        {
            Choices = Enumerable.Range(0, HumanInputLimits.MaxChoices + 1)
                .Select(index => new HumanInputChoice($"choice-{index}", $"Choice {index}"))
                .ToArray()
        };

        Assert.False(HumanInputRequestSnapshot.TryCapture(request, out var snapshot, out var validation));
        Assert.Null(snapshot);
        Assert.Contains(validation.Errors, error => error.Code == "request_snapshot_unbounded");
    }
}
