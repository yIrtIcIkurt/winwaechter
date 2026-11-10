using System.Collections;
using System.Security.AccessControl;
using System.Security.Principal;

namespace WinWaechter.Core.Sys;

/// <summary>Whether a file or folder can only be changed by administrators.</summary>
public interface IPathTrust
{
    /// <summary>True only for a non-link object owned and changeable only by admin principals; errors → false.</summary>
    bool IsAdminOnly(string path);
}

/// <summary>Allow-list check of owner and DACL: only Administrators, SYSTEM and TrustedInstaller may change the object.</summary>
public sealed class WinPathTrust : IPathTrust
{
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Any right that lets the holder change, create in, delete, re-permission or take over the object.
    const int WriteClass = (int)(FileSystemRights.WriteData | FileSystemRights.AppendData
            | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
            | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
            | FileSystemRights.TakeOwnership)
        | 0x10000000   // GENERIC_ALL
        | 0x40000000;  // GENERIC_WRITE

    public bool IsAdminOnly(string path)
    {
        try
        {
            // Links/junctions are refused outright: what they point to can differ from what was checked.
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(FileAttributes.ReparsePoint)) return false;
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity sec = attrs.HasFlag(FileAttributes.Directory)
                ? new DirectoryInfo(path).GetAccessControl(sections)
                : new FileInfo(path).GetAccessControl(sections);
            return Evaluate(sec.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier,
                sec.GetAccessRules(true, true, typeof(SecurityIdentifier)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Pure owner + rules evaluation (rules as returned by GetAccessRules with SecurityIdentifier).</summary>
    internal static bool Evaluate(SecurityIdentifier? owner, IEnumerable rules)
    {
        if (owner is null || !IsAdminPrincipal(owner)) return false;
        foreach (var item in rules)
        {
            if (item is not FileSystemAccessRule rule) return false;
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            // Inherit-only entries do not apply to this object (children are checked on their own);
            // this is also the only place CREATOR OWNER is acceptable.
            if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
            if (((int)rule.FileSystemRights & WriteClass) == 0) continue;
            if (rule.IdentityReference is not SecurityIdentifier sid || !IsAdminPrincipal(sid)) return false;
        }
        return true;
    }

    static bool IsAdminPrincipal(SecurityIdentifier sid) =>
        sid == Administrators || sid == LocalSystem || sid == TrustedInstaller;
}
