using WinWaechter.Core.Model;

namespace WinWaechter.Core.Security.Heuristics;

public static class Scoring
{
    public const int Load = 2, UnusualPlace = 1, UnsignedInPlace = 2, MinerName = 5, MinerCommandLine = 5,
        PoolPort = 2, HiddenTask = 1, PersistUnsignedInPlace = 2, Pua = 3;

    /// <summary>Under 3 Info, 3 to 5 Auffaellig, 6 and more Verdaechtig.</summary>
    public static Rating ToRating(int points) =>
        points >= 6 ? Rating.Verdaechtig : points >= 3 ? Rating.Auffaellig : Rating.Info;
}
