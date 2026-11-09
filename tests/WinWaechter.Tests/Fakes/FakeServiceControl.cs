using WinWaechter.Core.Sys;

namespace WinWaechter.Tests.Fakes;

// In-memory services; SetStartMode updates the entry and is recorded.
public sealed class FakeServiceControl : IServiceControl
{
    readonly Dictionary<string, ServiceInfo> _services = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Name, StartMode Mode, bool Delayed)> SetStartModeCalls { get; } = [];

    public FakeServiceControl Add(ServiceInfo s)
    {
        _services[s.Name] = s;
        return this;
    }

    public FakeServiceControl Add(string name, StartMode mode = StartMode.Automatic, bool delayed = false,
        bool running = true, string? imagePath = null, string? serviceDll = null, string? displayName = null) =>
        Add(new ServiceInfo(name, displayName ?? name, mode, delayed, running, imagePath, serviceDll));

    public bool Remove(string name) => _services.Remove(name);

    /// <summary>When set, List throws it.</summary>
    public Exception? ListError { get; set; }

    public IReadOnlyList<ServiceInfo> List() => ListError is { } e ? throw e : _services.Values.ToList();

    /// <summary>When set, Get throws it (simulates a service the SCM refuses to open).</summary>
    public Exception? GetError { get; set; }

    public ServiceInfo? Get(string name) => GetError is { } e ? throw e : _services.GetValueOrDefault(name);

    public void SetStartMode(string name, StartMode mode, bool delayed)
    {
        SetStartModeCalls.Add((name, mode, delayed));
        if (!_services.TryGetValue(name, out var s))
            throw new InvalidOperationException($"Dienst {name} nicht gefunden.");
        _services[name] = s with { Mode = mode, DelayedStart = mode == StartMode.Automatic && delayed };
    }
}
