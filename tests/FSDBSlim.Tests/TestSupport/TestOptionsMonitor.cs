namespace FSDBSlim.Tests.TestSupport;

using System;
using Microsoft.Extensions.Options;

public sealed class TestOptionsMonitor<T> : IOptionsMonitor<T>
    where T : class
{
    private T _currentValue;

    public TestOptionsMonitor(T value)
    {
        _currentValue = value;
    }

    public T CurrentValue => _currentValue;

    public T Get(string? name) => _currentValue;

    public IDisposable OnChange(Action<T, string?> listener) => NullDisposable.Instance;

    public void Set(T value) => _currentValue = value;

    private sealed class NullDisposable : IDisposable
    {
        public static NullDisposable Instance { get; } = new();
        public void Dispose()
        {
        }
    }
}
