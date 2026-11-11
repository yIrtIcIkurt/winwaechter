namespace WinWaechter.Core.Programs;

/// <summary>Starts a program with the signed-in user's normal (unelevated) rights and does not wait for it.</summary>
public interface IUserLauncher
{
    /// <summary>Throws (UserLaunchException or InvalidOperationException) when that is not possible;
    /// never falls back to elevated rights.</summary>
    void StartAsUser(string exe, string arguments);
}

/// <summary>Uses the token of the user's Explorer in the console session (<see cref="UserProcess"/>).</summary>
public sealed class UserTokenLauncher(string? userSid) : IUserLauncher
{
    public void StartAsUser(string exe, string arguments) => UserProcess.StartAsUser(exe, arguments, userSid);
}
