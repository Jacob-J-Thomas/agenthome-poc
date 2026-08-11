using EmbodySense.Core.Common.Triggers;

namespace EmbodySense.Core.Common.Tests.Triggers;

public sealed class TriggerDispatchOperationIdTests
{
    [Fact]
    public void Validator_accepts_only_the_exact_prefix_and_lowercase_sha256_shape()
    {
        var valid = TriggerDispatchOperationId.Prefix + new string('a', TriggerDeliveryLimits.Sha256HexCharacters);

        Assert.True(TriggerDispatchOperationId.IsValid(valid));
        Assert.False(TriggerDispatchOperationId.IsValid(null));
        Assert.False(TriggerDispatchOperationId.IsValid(string.Empty));
        Assert.False(TriggerDispatchOperationId.IsValid("Trigger-" + new string('a', TriggerDeliveryLimits.Sha256HexCharacters)));
        Assert.False(TriggerDispatchOperationId.IsValid(TriggerDispatchOperationId.Prefix + new string('A', TriggerDeliveryLimits.Sha256HexCharacters)));
        Assert.False(TriggerDispatchOperationId.IsValid(TriggerDispatchOperationId.Prefix + new string('a', TriggerDeliveryLimits.Sha256HexCharacters - 1)));
        Assert.False(TriggerDispatchOperationId.IsValid(TriggerDispatchOperationId.Prefix + new string('a', TriggerDeliveryLimits.Sha256HexCharacters - 1) + "g"));
    }
}
