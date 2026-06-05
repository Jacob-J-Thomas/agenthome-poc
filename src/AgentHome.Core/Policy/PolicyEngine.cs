using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentHome.Core.Workspace;

namespace AgentHome.Core.Policy;

public sealed class PolicyEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly WorkspacePaths _paths;

    public PolicyEngine(WorkspacePaths paths)
    {
        _paths = paths;
    }

    public async Task<PolicyEvaluation> EvaluateAsync(string action, string target, CancellationToken cancellationToken = default)
    {
        var normalizedAction = NormalizeAction(action);
        var normalizedTarget = NormalizeTarget(target);
        var permissionsPath = _paths.AgentFile("permissions.json");

        var document = await TryReadPermissionsAsync(permissionsPath, cancellationToken);
        if (document is null)
        {
            return new PolicyEvaluation(
                normalizedAction,
                normalizedTarget,
                PermissionDecision.Prompt,
                "fallback",
                "permissions.json is missing or invalid; ambiguity resolves to Prompt.");
        }

        foreach (var rule in document.Rules)
        {
            if (!ActionMatches(rule.Action, normalizedAction))
            {
                continue;
            }

            if (!GlobMatches(rule.Target, normalizedTarget))
            {
                continue;
            }

            return new PolicyEvaluation(
                normalizedAction,
                normalizedTarget,
                rule.Decision,
                "rule",
                rule.Reason);
        }

        return new PolicyEvaluation(
            normalizedAction,
            normalizedTarget,
            document.DefaultDecision,
            "defaultDecision",
            "No policy rule matched.");
    }

    private static async Task<PermissionsDocument?> TryReadPermissionsAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);
            return JsonSerializer.Deserialize<PermissionsDocument>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeAction(string action)
    {
        return action.Trim().ToLowerInvariant();
    }

    private static string NormalizeTarget(string target)
    {
        var normalized = target.Trim().Replace('\\', '/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }

    private static bool ActionMatches(string ruleAction, string requestedAction)
    {
        var normalizedRuleAction = NormalizeAction(ruleAction);
        return normalizedRuleAction == "**" || normalizedRuleAction == "*" || normalizedRuleAction == requestedAction;
    }

    private static bool GlobMatches(string pattern, string target)
    {
        var normalizedPattern = NormalizeTarget(pattern);
        var regex = "^" + Regex.Escape(normalizedPattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*") + "$";

        return Regex.IsMatch(target, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
