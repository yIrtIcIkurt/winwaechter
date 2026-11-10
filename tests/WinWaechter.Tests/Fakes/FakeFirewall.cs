using WinWaechter.Core.Security.Checks;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeFirewall : IFirewall
{
    public Dictionary<FirewallProfile, bool> State { get; } = new()
    {
        [FirewallProfile.Domain] = true, [FirewallProfile.Private] = true, [FirewallProfile.Public] = true,
    };
    public List<(FirewallProfile Profile, bool On)> SetCalls { get; } = [];
    public bool Throw { get; set; }

    public bool IsEnabled(FirewallProfile p) => Throw ? throw new InvalidOperationException("Firewall nicht lesbar") : State[p];

    public void SetEnabled(FirewallProfile p, bool on)
    {
        if (Throw) throw new InvalidOperationException("Firewall nicht änderbar");
        SetCalls.Add((p, on));
        State[p] = on;
    }
}
