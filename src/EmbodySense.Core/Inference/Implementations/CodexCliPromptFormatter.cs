using System.Text;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

internal static class CodexCliPromptFormatter
{
    public static string Format(LlmInferenceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var builder = new StringBuilder();

        foreach (var message in request.Messages)
        {
            builder.Append(GetRoleLabel(message.Role));
            builder.AppendLine(":");
            builder.AppendLine(message.Content);
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string GetRoleLabel(LlmMessageRole role)
    {
        return role switch
        {
            LlmMessageRole.System => "System",
            LlmMessageRole.User => "User",
            LlmMessageRole.Assistant => "Assistant",
            LlmMessageRole.Tool => "Tool",
            _ => "Message"
        };
    }
}
