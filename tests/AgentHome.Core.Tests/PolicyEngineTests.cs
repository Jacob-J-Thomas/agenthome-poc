using AgentHome.Core.Policy;
using AgentHome.Core.Workspace;
using Xunit;

namespace AgentHome.Core.Tests;

public sealed class PolicyEngineTests
{
    [Fact]
    public async Task EvaluateAsyncReturnsPromptWhenPolicyIsMissing()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(workspace.Path(".agent"));
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("file.write", "workspace/shared/demo.txt");

        Assert.Equal(PermissionDecision.Prompt, evaluation.Decision);
        Assert.Equal("fallback", evaluation.Source);
    }

    [Fact]
    public async Task EvaluateAsyncReturnsPromptWhenPolicyJsonIsInvalid()
    {
        using var workspace = TestWorkspace.Create();
        Directory.CreateDirectory(workspace.Path(".agent"));
        await File.WriteAllTextAsync(workspace.Path(".agent/permissions.json"), "{");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("file.write", "workspace/shared/demo.txt");

        Assert.Equal(PermissionDecision.Prompt, evaluation.Decision);
        Assert.Equal("fallback", evaluation.Source);
    }

    [Fact]
    public async Task EvaluateAsyncUsesDefaultDecisionWhenNoRuleMatches()
    {
        using var workspace = TestWorkspace.Create();
        await WritePermissionsAsync(workspace, """
{
  "version": 1,
  "defaultDecision": "Deny",
  "rules": []
}
""");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("file.write", "workspace/shared/demo.txt");

        Assert.Equal(PermissionDecision.Deny, evaluation.Decision);
        Assert.Equal("defaultDecision", evaluation.Source);
    }

    [Fact]
    public async Task EvaluateAsyncUsesFirstMatchingRule()
    {
        using var workspace = TestWorkspace.Create();
        await WritePermissionsAsync(workspace, """
{
  "version": 1,
  "defaultDecision": "Deny",
  "rules": [
    {
      "action": "file.write",
      "target": "workspace/shared/**",
      "decision": "Prompt",
      "reason": "First matching rule."
    },
    {
      "action": "file.write",
      "target": "workspace/shared/demo.txt",
      "decision": "Allow",
      "reason": "Later more specific rule."
    }
  ]
}
""");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("file.write", "workspace/shared/demo.txt");

        Assert.Equal(PermissionDecision.Prompt, evaluation.Decision);
        Assert.Equal("First matching rule.", evaluation.Reason);
    }

    [Fact]
    public async Task EvaluateAsyncNormalizesActionCaseAndTargetSlashes()
    {
        using var workspace = TestWorkspace.Create();
        await WritePermissionsAsync(workspace, """
{
  "version": 1,
  "defaultDecision": "Deny",
  "rules": [
    {
      "action": "file.write",
      "target": "workspace/shared/**",
      "decision": "Allow"
    }
  ]
}
""");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("FILE.WRITE", ".\\workspace\\shared\\demo.txt");

        Assert.Equal(PermissionDecision.Allow, evaluation.Decision);
        Assert.Equal("file.write", evaluation.Action);
        Assert.Equal("workspace/shared/demo.txt", evaluation.Target);
    }

    [Fact]
    public async Task EvaluateAsyncSupportsWildcardAction()
    {
        using var workspace = TestWorkspace.Create();
        await WritePermissionsAsync(workspace, """
{
  "version": 1,
  "defaultDecision": "Deny",
  "rules": [
    {
      "action": "*",
      "target": "workspace/generated/**",
      "decision": "Allow"
    }
  ]
}
""");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var evaluation = await engine.EvaluateAsync("file.write", "workspace/generated/report.md");

        Assert.Equal(PermissionDecision.Allow, evaluation.Decision);
    }

    [Fact]
    public async Task EvaluateAsyncSingleStarDoesNotCrossDirectoryBoundary()
    {
        using var workspace = TestWorkspace.Create();
        await WritePermissionsAsync(workspace, """
{
  "version": 1,
  "defaultDecision": "Deny",
  "rules": [
    {
      "action": "file.read",
      "target": "workspace/shared/*.md",
      "decision": "Allow"
    }
  ]
}
""");
        var engine = new PolicyEngine(new WorkspacePaths(workspace.Root));

        var nestedEvaluation = await engine.EvaluateAsync("file.read", "workspace/shared/nested/readme.md");
        var directEvaluation = await engine.EvaluateAsync("file.read", "workspace/shared/readme.md");

        Assert.Equal(PermissionDecision.Deny, nestedEvaluation.Decision);
        Assert.Equal(PermissionDecision.Allow, directEvaluation.Decision);
    }

    private static async Task WritePermissionsAsync(TestWorkspace workspace, string content)
    {
        Directory.CreateDirectory(workspace.Path(".agent"));
        await File.WriteAllTextAsync(workspace.Path(".agent/permissions.json"), content);
    }
}
