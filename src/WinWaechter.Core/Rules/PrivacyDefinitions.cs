using WinWaechter.Core.Sys;

namespace WinWaechter.Core.Rules;

/// <summary>One registry value the privacy feature may touch; always written as REG_DWORD.</summary>
public sealed record PrivacyValue(Hive Hive, string Key, string Name, int Target);

/// <summary>Value that shows what Windows really uses (e.g. policy-overridden); "at most" counts as already minimal.</summary>
public sealed record PrivacyEffective(Hive Hive, string Key, string Name, int AtMost);

public sealed record PrivacySetting(string Id, string Title, string Explanation, bool Recommended, string? Requires,
    IReadOnlyList<PrivacyValue> Values, PrivacyEffective? Effective, string? Group = null);

/// <summary>Group keys a privacy setting can belong to (null = general privacy).</summary>
public static class PrivacyGroups
{
    public const string Ads = "werbung";
}

/// <summary>One preinstalled app that may be offered for removal. Package = package Name, optional trailing "*".</summary>
public sealed record BloatAppRule(string Package, string Title, bool Recommended);
