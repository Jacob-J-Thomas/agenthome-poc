using EmbodySense.Core.Inference.Interfaces;
using EmbodySense.Core.Inference.Models;

namespace EmbodySense.Core.Inference.Implementations;

internal sealed class CodexCliInferenceClient : ILlmInferenceClient
{
    private readonly LlmInferenceClientOptions _options;
    private readonly ICodexCliProcessRunner _processRunner;

    public CodexCliInferenceClient(LlmInferenceClientOptions options, ICodexCliProcessRunner? processRunner = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _processRunner = processRunner ?? new CodexCliProcessRunner();
    }

    public async Task<LlmInferenceResponse> GenerateAsync(
        LlmInferenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var prompt = CodexCliPromptFormatter.Format(request);
        var startInfo = CodexCliProcessStartInfoFactory.Create(_options);
        var result = await _processRunner.RunAsync(startInfo, prompt, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(CreateFailureMessage(result.ExitCode, result.Output, result.Error));
        }

        return new LlmInferenceResponse(
            result.Output.TrimEnd(),
            LlmInferenceSurface.OpenAiCodex,
            _options.Model);
    }

    private static string CreateFailureMessage(int exitCode, string output, string error)
    {
        var details = string.IsNullOrWhiteSpace(error) ? output : error;

        return string.IsNullOrWhiteSpace(details)
            ? $"Codex CLI exited with code {exitCode}."
            : $"Codex CLI exited with code {exitCode}: {details.Trim()}";
    }
}
