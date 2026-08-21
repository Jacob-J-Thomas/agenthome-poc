namespace EmbodySense.Core.Application.Credentials;

/// <summary>Closes one trusted callback after its first invocation or provider return.</summary>
internal sealed class SingleUseCredentialTrustedUseConsumer(ICredentialTrustedUseConsumer inner) : ICredentialTrustedUseConsumer
{
    private readonly ICredentialTrustedUseConsumer _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    private int _state = 1;
    private int _invocationCount;
    private int _invocationCompletedSuccessfully;

    internal int InvocationCount => Volatile.Read(ref _invocationCount);
    internal bool InvocationCompletedSuccessfully => Volatile.Read(ref _invocationCompletedSuccessfully) == 1;

    public void Use(ReadOnlySpan<byte> credential)
    {
        var invocation = Interlocked.Increment(ref _invocationCount);
        if (invocation != 1 || Interlocked.CompareExchange(ref _state, 2, 1) != 1)
        {
            throw new InvalidOperationException("A credential provider attempted to invoke a single-use consumer more than once or after return.");
        }

        _inner.Use(credential);
        Volatile.Write(ref _invocationCompletedSuccessfully, 1);
    }

    internal void Close() => Interlocked.Exchange(ref _state, 0);
}
