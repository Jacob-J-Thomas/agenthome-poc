namespace EmbodySense.IntegrationTests.Architecture;

public sealed class WebHumanReviewBoundaryArchitectureTests
{
    private static readonly string[] _connectionOwnedApprovalTokens =
    [
        "WebApprovalCoordinator",
        "ApprovalScope",
        "BeginApprovalScope",
        "ConnectionId",
        "PendingApproval",
        "WebPendingApproval"
    ];

    [Fact]
    public void Web_human_review_surface_does_not_use_connection_owned_approval_authority()
    {
        var root = FindRepositoryRoot();
        var relativePaths = new[]
        {
            "src/EmbodySense.Web/Controllers/HumanReviewsController.cs",
            "src/EmbodySense.Web/Services/IWebHumanReviewRuntime.cs",
            "src/EmbodySense.Web/Services/WebHumanReviewDecisionAuthorizationProvider.cs"
        };

        var violations = relativePaths
            .Select(path => (Path: path, Text: File.ReadAllText(Path.Combine(root, path))))
            .SelectMany(source => _connectionOwnedApprovalTokens
                .Where(token => source.Text.Contains(token, StringComparison.Ordinal))
                .Select(token => $"{source.Path} contains {token}"))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Web_human_review_source_does_not_duplicate_the_startup_owned_reviewer_role()
    {
        var root = FindRepositoryRoot();
        var webDirectory = Path.Combine(root, "src", "EmbodySense.Web");
        var violations = Directory
            .EnumerateFiles(webDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("governed-reviewer", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(root, file))
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void Web_human_review_hub_is_notification_only()
    {
        var root = FindRepositoryRoot();
        var hub = File.ReadAllText(Path.Combine(root, "src", "EmbodySense.Web", "Hubs", "WebSessionHub.cs"));
        var client = File.ReadAllText(Path.Combine(root, "src", "EmbodySense.Web", "Hubs", "IWebSessionClient.cs"));

        Assert.Contains("HumanReviewChanged", client, StringComparison.Ordinal);
        Assert.DoesNotContain("DecideHumanReview", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("ApproveHumanReview", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("RejectHumanReview", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelHumanReview", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestHumanReviewInformation", hub, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_human_review_reuses_one_retained_host_and_rejects_duplicate_json_properties()
    {
        var root = FindRepositoryRoot();
        var program = File.ReadAllText(Path.Combine(root, "src", "EmbodySense.Web", "Program.cs"));
        var host = File.ReadAllText(Path.Combine(root, "src", "EmbodySense.Web", "WebAgentRuntimeHost.cs"));

        Assert.Equal(1, Count(program, "new WebAgentRuntimeHost("));
        Assert.Contains("IWebHumanReviewRuntime", program, StringComparison.Ordinal);
        Assert.Contains("WithHumanReviewDecisionAuthorizationProvider", program, StringComparison.Ordinal);
        Assert.Equal(1, Count(host, "new CustomLoopRunStoreProvider("));
        Assert.Contains("IWebHumanReviewRuntime", host, StringComparison.Ordinal);
        Assert.Contains("AllowDuplicateProperties = false", program, StringComparison.Ordinal);
    }

    [Fact]
    public void Web_custom_loop_execution_cannot_reach_connection_owned_approval()
    {
        var root = FindRepositoryRoot();
        var host = File.ReadAllText(Path.Combine(root, "src", "EmbodySense.Web", "WebAgentRuntimeHost.cs"));

        Assert.Contains("WithoutLegacyCustomLoopToolApprovals", host, StringComparison.Ordinal);
        Assert.Equal(1, Count(host, "BeginApprovalScope("));
    }

    private static int Count(string source, string value)
    {
        var count = 0;
        var offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EmbodySense.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
