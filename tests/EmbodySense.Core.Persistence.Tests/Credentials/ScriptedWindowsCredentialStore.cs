using System.Security.Cryptography;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class ScriptedWindowsCredentialStore : IDisposable
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly Queue<Func<string, byte[], ScriptedCredentialStoreStatus>> _writes = new();
    private readonly Queue<Func<string, ScriptedCredentialStoreStatus>> _deletes = new();
    private readonly Queue<ScriptedCredentialStoreStatus> _reads = new();
    private readonly object _sync = new();

    internal ScriptedWindowsCredentialStore(bool isSupported = true, int maxValueByteLength = 2_560)
    {
        IsSupported = isSupported;
        MaxValueByteLength = maxValueByteLength;
    }

    public bool IsSupported { get; }
    public int MaxValueByteLength { get; }

    public ScriptedCredentialStoreStatus Probe(string target)
    {
        lock (_sync)
        {
            if (_reads.TryDequeue(out var scripted) && scripted != ScriptedCredentialStoreStatus.Success)
            {
                return scripted;
            }

            return _values.ContainsKey(target) ? ScriptedCredentialStoreStatus.Success : ScriptedCredentialStoreStatus.Missing;
        }
    }

    public ScriptedCredentialReadResult Read(string target)
    {
        lock (_sync)
        {
            if (_reads.TryDequeue(out var scripted) && scripted != ScriptedCredentialStoreStatus.Success)
            {
                return scripted == ScriptedCredentialStoreStatus.Missing ? ScriptedCredentialReadResult.Missing() : ScriptedCredentialReadResult.Failed(scripted);
            }

            return _values.TryGetValue(target, out var value) ? ScriptedCredentialReadResult.Found(value.ToArray()) : ScriptedCredentialReadResult.Missing();
        }
    }

    public ScriptedCredentialStoreStatus Write(string target, byte[] value)
    {
        lock (_sync)
        {
            if (_writes.TryDequeue(out var scripted))
            {
                return scripted(target, value);
            }

            Set(target, value);
            return ScriptedCredentialStoreStatus.Success;
        }
    }

    public ScriptedCredentialStoreStatus Delete(string target)
    {
        lock (_sync)
        {
            if (_deletes.TryDequeue(out var scripted))
            {
                return scripted(target);
            }

            return Remove(target) ? ScriptedCredentialStoreStatus.Success : ScriptedCredentialStoreStatus.Missing;
        }
    }

    internal void Seed(string target, byte[] value)
    {
        lock (_sync)
        {
            Set(target, value);
        }
    }

    internal byte[] Snapshot(string target)
    {
        lock (_sync)
        {
            return _values.TryGetValue(target, out var value) ? value.ToArray() : [];
        }
    }

    internal void EnqueueRead(ScriptedCredentialStoreStatus status)
    {
        lock (_sync)
        {
            _reads.Enqueue(status);
        }
    }

    internal void EnqueueWrite(Func<string, byte[], ScriptedCredentialStoreStatus> operation)
    {
        lock (_sync)
        {
            _writes.Enqueue(operation);
        }
    }

    internal void EnqueueDelete(Func<string, ScriptedCredentialStoreStatus> operation)
    {
        lock (_sync)
        {
            _deletes.Enqueue(operation);
        }
    }

    internal ScriptedCredentialStoreStatus MutateThenFail(string target, byte[] value)
    {
        Set(target, value);
        return ScriptedCredentialStoreStatus.Unavailable;
    }

    internal ScriptedCredentialStoreStatus RemoveThenFail(string target)
    {
        Remove(target);
        return ScriptedCredentialStoreStatus.Unavailable;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var value in _values.Values)
            {
                CryptographicOperations.ZeroMemory(value);
            }

            _values.Clear();
            _writes.Clear();
            _deletes.Clear();
            _reads.Clear();
        }
    }

    private void Set(string target, byte[] value)
    {
        Remove(target);
        _values[target] = value.ToArray();
    }

    private bool Remove(string target)
    {
        if (!_values.Remove(target, out var removed))
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(removed);
        return true;
    }
}
