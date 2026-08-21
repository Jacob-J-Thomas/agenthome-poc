using System.Text;
using EmbodySense.Core.Common.Inference.Profiles;

namespace EmbodySense.Core.Application.Inference.Profiles;

internal sealed class BoundedInferenceResponseBuffer
{
    private readonly StringBuilder _value = new();
    private readonly List<string> _chunks = new();
    private readonly object _gate = new();
    private bool _failed;
    private bool _sealed;

    internal Task AppendAsync(string chunk, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_failed
                || _sealed
                || chunk is null
                || _chunks.Count == GovernedModelContractLimits.MaxProviderOutputChunks
                || _value.Length + chunk.Length > GovernedModelContractLimits.MaxProviderOutputCharacters)
            {
                _failed = true;
                throw new InvalidOperationException("Provider streaming output exceeded the bounded exact response contract.");
            }
            _chunks.Add(chunk);
            _value.Append(chunk);
        }
        return Task.CompletedTask;
    }

    internal bool TrySeal(string? terminalOutput, out IReadOnlyList<string> exactChunks)
    {
        lock (_gate)
        {
            _sealed = true;
            exactChunks = Array.AsReadOnly(_chunks.ToArray());
            if (_failed || terminalOutput is null || !string.Equals(_value.ToString(), terminalOutput, StringComparison.Ordinal))
            {
                _failed = true;
                return false;
            }
            return true;
        }
    }

    internal bool IsExactSealedChunks(IReadOnlyList<string> exactChunks)
    {
        lock (_gate)
        {
            return _sealed
                && !_failed
                && _chunks.SequenceEqual(exactChunks, StringComparer.Ordinal);
        }
    }
}
