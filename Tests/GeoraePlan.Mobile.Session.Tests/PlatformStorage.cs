// Only platform persistence is substituted. Tests compile the real session/access code.
internal static class Preferences
{
    public static MemoryPreferences Default { get; } = new();
}
internal sealed class MemoryPreferences
{
    private readonly Dictionary<string, object> _values = new();
    public T Get<T>(string key, T fallback) => _values.TryGetValue(key, out var value) ? (T)value : fallback;
    public void Set<T>(string key, T value) where T : notnull => _values[key] = value;
    public void Remove(string key) => _values.Remove(key);
    public void Reset() => _values.Clear();
}
internal static class SecureStorage
{
    public static MemorySecureStorage Default { get; } = new();
}
internal sealed class MemorySecureStorage
{
    private readonly Dictionary<string, string> _values = new();
    public bool FailWrites { get; set; }
    public Task SetAsync(string key, string value)
    {
        if (FailWrites) throw new IOException("simulated secure storage failure");
        _values[key] = value; return Task.CompletedTask;
    }
    public Task<string?> GetAsync(string key) => Task.FromResult(_values.GetValueOrDefault(key));
    public bool Remove(string key) => _values.Remove(key);
    public void Reset() { _values.Clear(); FailWrites = false; }
}
