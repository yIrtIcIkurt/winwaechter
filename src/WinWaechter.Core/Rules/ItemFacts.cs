namespace WinWaechter.Core.Rules;

/// <summary>What we know about an autostart, service, task or process when asking the rules.</summary>
public sealed record ItemFacts(string Name, string? ExePath, string? Publisher, string? CommandLine);
