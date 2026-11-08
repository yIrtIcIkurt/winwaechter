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

    /// <summary>
    /// Sets the owner to BUILTIN\Administrators (only the owner, the DACL stays inherited). Needs an
    /// elevated token. Never throws: a failure is logged, and the owner check then fails closed.
    /// </summary>
    public static void SetAdmins(string path)
    {
        try
        {
            var sec = new FileSecurity();
            sec.SetOwner(Admins);
            new FileInfo(path).SetAccessControl(sec);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or PlatformNotSupportedException)
        {
            Logging.FileLog.Warn($"Besitzer von „{path}“ konnte nicht gesetzt werden", ex);
        }
    }
}
