namespace EmbodySense.Core.Clients.CommandActions;

internal sealed class CommandActionOutputBudget
{
    private readonly object _gate = new();
    private readonly int _maximum;
    private int _standardOutputBytes;
    private int _standardErrorBytes;
    private int _total;

    internal CommandActionOutputBudget(int maximum) => _maximum = maximum;

    internal int StandardOutputBytes { get { lock (_gate) { return _standardOutputBytes; } } }

    internal int StandardErrorBytes { get { lock (_gate) { return _standardErrorBytes; } } }

    internal void Account(int byteCount, bool standardOutput)
    {
        lock (_gate)
        {
            var accepted = Math.Min(byteCount, _maximum + 1 - _total);
            _total += accepted;
            if (standardOutput)
            {
                _standardOutputBytes += accepted;
            }
            else
            {
                _standardErrorBytes += accepted;
            }
            if (accepted != byteCount || _total > _maximum)
            {
                throw new CommandActionOutputLimitException();
            }
        }
    }
}
