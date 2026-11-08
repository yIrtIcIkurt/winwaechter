using System.Security.AccessControl;
using System.Security.Principal;

namespace WinWaechter.Core.Sys;

public static class FileOwner
{
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>
    /// True when the file is owned by BUILTIN\Administrators or NT AUTHORITY\SYSTEM.
    /// Anything unreadable counts as not trusted.
    /// </summary>
    public static bool IsAdminOrSystem(string path)
    {
        try
        {
            var owner = new FileInfo(path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
            return owner is SecurityIdentifier sid && (sid == Admins || sid == SystemSid);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or IdentityNotMappedException or PlatformNotSupportedException)
        {
            return false;
        }
    }
}
