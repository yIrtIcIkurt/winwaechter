using WinWaechter.Core.Performance;

namespace WinWaechter.Tests.Fakes;

public sealed class FakeBroadcast : ISettingBroadcast
{
    public int Count { get; private set; }
    public void ImmersiveColorSetChanged() => Count++;
}
