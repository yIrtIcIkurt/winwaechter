using System.Security.AccessControl;
using System.Security.Principal;

namespace WinWaechter.Core.Sys;

/// <summary>Whether a file or folder can only be changed by administrators.</summary>
public interface IPathTrust
{
    /// <summary>True only when the owner is an admin principal and no non-admin principal may change it; errors → false.</summary>
    bool IsAdminOnly(string path);
}

/// <summary>Reads owner and DACL of a file or folder (links followed, i.e. the target is checked).</summary>
public sealed class WinPathTrust : IPathTrust
{
    static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier TrustedInstaller =
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464");

    // Any right that lets the holder change, create in, delete, re-permission or take over the object.
    const FileSystemRights ChangeRights = FileSystemRights.WriteData | FileSystemRights.AppendData
        | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes | FileSystemRights.Delete
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions
        | FileSystemRights.TakeOwnership;
    const int GenericAll = 0x10000000, GenericWrite = 0x40000000;

    readonly HashSet<SecurityIdentifier> _nonAdmins;

    public WinPathTrust(IUserContext user)
    {
        _nonAdmins =
        [
            new(WellKnownSidType.BuiltinUsersSid, null),
            new(WellKnownSidType.AuthenticatedUserSid, null),
            new(WellKnownSidType.WorldSid, null),
            new(WellKnownSidType.InteractiveSid, null),
        ];
        try { _nonAdmins.Add(new SecurityIdentifier(user.Sid)); }
        catch { /* unreadable SID: the fixed groups still apply */ }
    }

    public bool IsAdminOnly(string path)
    {
        try
        {
            FileSystemSecurity sec = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                : File.Exists(path)
                    ? new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                    : throw new FileNotFoundException(path);

            if (sec.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
                || !(owner == Administrators || owner == LocalSystem || owner == TrustedInstaller))
                return false;

            foreach (FileSystemAccessRule rule in sec.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                // Inherit-only entries do not apply to this object itself (children are checked on their own).
                if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly)) continue;
                if (rule.IdentityReference is not SecurityIdentifier sid || !_nonAdmins.Contains(sid)) continue;
                var mask = (int)rule.FileSystemRights;
                if ((mask & (int)ChangeRights) != 0 || (mask & (GenericAll | GenericWrite)) != 0) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
