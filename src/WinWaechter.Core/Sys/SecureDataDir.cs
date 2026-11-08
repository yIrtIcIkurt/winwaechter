using System.Security.AccessControl;
using System.Security.Principal;

namespace WinWaechter.Core.Sys;

public static class SecureDataDir
{
    // Owner = Administrators; Admins + SYSTEM full control, Users read/execute. Needs admin.
    public static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

        var sec = new DirectorySecurity();
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(true, false); // no inherited rules
        foreach (FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(SecurityIdentifier)))
            sec.RemoveAccessRuleSpecific(r);
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(system, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(sec);
        return path;
    }
}
