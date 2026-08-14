using EmbodySense.Core.Application.Loops.Sequential;

namespace EmbodySense.Core.Application.Tests.Loops.Sequential;

public sealed class GovernedLoopSequentialAuditOperationIdTests
{
    [Fact]
    public void Admission_node_and_terminal_identities_are_deterministic_domain_separated_and_bounded()
    {
        var receipt = new string('a', 64);
        var binding = new string('b', 64);

        var first = GovernedLoopSequentialAuditOperationId.ForAdmission(receipt, binding);
        var replay = GovernedLoopSequentialAuditOperationId.ForAdmission(receipt, binding);
        var node = GovernedLoopSequentialAuditOperationId.ForNodeOutcome(receipt);
        var terminal = GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(receipt);
        var terminalReplay = GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(receipt);

        Assert.Equal(first, replay);
        Assert.Equal(terminal, terminalReplay);
        Assert.NotEqual(first, node);
        Assert.NotEqual(first, terminal);
        Assert.NotEqual(node, terminal);
        Assert.StartsWith("sequential-audit-", first, StringComparison.Ordinal);
        Assert.Equal("sequential-audit-".Length + 64, first.Length);
        Assert.All(first["sequential-audit-".Length..], character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
        Assert.StartsWith("sequential-audit-", terminal, StringComparison.Ordinal);
        Assert.Equal("sequential-audit-".Length + 64, terminal.Length);
        Assert.All(terminal["sequential-audit-".Length..], character => Assert.True(character is >= '0' and <= '9' or >= 'a' and <= 'f'));
    }

    [Fact]
    public void Every_exact_evidence_coordinate_changes_its_operation_identity()
    {
        var receipt = new string('a', 64);
        var binding = new string('b', 64);
        var changedReceipt = new string('c', 64);
        var changedBinding = new string('d', 64);

        Assert.NotEqual(
            GovernedLoopSequentialAuditOperationId.ForAdmission(receipt, binding),
            GovernedLoopSequentialAuditOperationId.ForAdmission(changedReceipt, binding));
        Assert.NotEqual(
            GovernedLoopSequentialAuditOperationId.ForAdmission(receipt, binding),
            GovernedLoopSequentialAuditOperationId.ForAdmission(receipt, changedBinding));
        Assert.NotEqual(
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(receipt),
            GovernedLoopSequentialAuditOperationId.ForNodeOutcome(changedReceipt));
        Assert.NotEqual(
            GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(receipt),
            GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(changedReceipt));
    }

    [Theory]
    [InlineData("")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Hash_inputs_require_exact_lowercase_sha256_shape(string invalid)
    {
        Assert.Throws<ArgumentException>(() => GovernedLoopSequentialAuditOperationId.ForAdmission(invalid, new string('b', 64)));
        Assert.Throws<ArgumentException>(() => GovernedLoopSequentialAuditOperationId.ForAdmission(new string('a', 64), invalid));
        Assert.Throws<ArgumentException>(() => GovernedLoopSequentialAuditOperationId.ForNodeOutcome(invalid));
        Assert.Throws<ArgumentException>(() => GovernedLoopSequentialAuditOperationId.ForTerminalLifecycle(invalid));
    }
}
