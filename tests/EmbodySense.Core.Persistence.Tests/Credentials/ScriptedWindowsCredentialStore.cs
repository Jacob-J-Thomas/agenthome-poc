using System.Security.Cryptography;
using EmbodySense.Core.Persistence.Credentials;

namespace EmbodySense.Core.Persistence.Tests.Credentials;

internal sealed class ScriptedWindowsCredentialStore : IWindowsCredentialStore, IDisposable
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    private readonly Queue<Func<string, byte[], WindowsCredentialStoreStatus>> _writes = new();
    private readonly Queue<Func<string, WindowsCredentialStoreStatus>> _deletes = new();
    private readonly Queue<WindowsCredentialStoreStatus> _reads = new();
    private readonly object _sync = new();

    internal ScriptedWindowsCredentialStore(bool isSupported = true, int maxValueByteLength = 2_560)
    {
        IsSupported = isSupported;
        MaxValueByteLength = maxValueByteLength;
    }

    public bool IsSupported { get; }
    public int MaxValueByteLength { get; }

    public WindowsCredentialStoreStatus Probe(string target)
    {
        lock (_sync)
        {
            if (_reads.TryDequeue(out var scripted) && scripted != WindowsCredentialStoreStatus.Success)
            {
                return scripted;
            }

            return _values.ContainsKey(target) ? WindowsCredentialStoreStatus.Success : WindowsCredentialStoreStatus.Missing;
        }
    }

    public WindowsCredentialReadResult Read(string target)
    {
        lock (_sync)
        {
            if (_reads.TryDequeue(out var scripted) && scripted != WindowsCredentialStoreStatus.Success)
            {
                return scripted == WindowsCredentialStoreStatus.Missing ? WindowsCredentialReadResult.Missing() : WindowsCredentialReadResult.Failed(scripted);
            }

            return _values.TryGetValue(target, out var value) ? WindowsCredentialReadResult.Found(value.ToArray()) : WindowsCredentialReadResult.Missing();
        }
    }

    public WindowsCredentialStoreStatus Write(string target, byte[] value)
    {
        lock (_sync)
        {
            if (_writes.TryDequeue(out var scripted))
            {
                return scripted(target, value);
            }

            Set(target, value);
            return WindowsCredentialStoreStatus.Success;
        }
    }

    public WindowsCredentialStoreStatus Delete(string target)
    {
        lock (_sync)
        {
            if (_deletes.TryDequeue(out var scripted))
            {
                return scripted(target);
            }

            return Remove(target) ? WindowsCredentialStoreStatus.Success : WindowsCredentialStoreStatus.Missing;
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

    internal void EnqueueRead(WindowsCredentialStoreStatus status)
    {
        lock (_sync)
        {
            _reads.Enqueue(status);
        }
    }

    internal void EnqueueWrite(Func<string, byte[], WindowsCredentialStoreStatus> operation)
    {
        lock (_sync)
        {
            _writes.Enqueue(operation);
        }
    }

    internal void EnqueueDelete(Func<string, WindowsCredentialStoreStatus> operation)
    {
        lock (_sync)
        {
            _deletes.Enqueue(operation);
        }
    }

    internal WindowsCredentialStoreStatus MutateThenFail(string target, byte[] value)
    {
        Set(target, value);
        return WindowsCredentialStoreStatus.Unavailable;
    }

    internal WindowsCredentialStoreStatus RemoveThenFail(string target)
    {
        Remove(target);
        return WindowsCredentialStoreStatus.Unavailable;
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
