namespace EmbodySense.Core.Persistence.Loops.GraphAuthoring.Models;

internal sealed record HumanInputResponsePolicyJson(string? Kind, int? RequiredResponseCount, string[]? OrderedRoleIds);
