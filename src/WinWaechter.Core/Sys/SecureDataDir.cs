using System.Security.AccessControl;
using System.Security.Principal;

namespace WinWaechter.Core.Sys;

/// <summary>
/// Secures %ProgramData%\WinWaechter. Root: owner Administrators; Admins + SYSTEM full control,
/// Users read/execute, no inherited rules. Every subfolder: owner Administrators, no own rules,
/// inherits the root. Junctions/symlinks are refused at the root (and its parents) and removed
/// inside. Files are not touched here: a file a normal user planted keeps its owner and is then
/// ignored by the owner check of the journal loader (taking it over would make it look trusted).
/// Needs admin.
/// </summary>
public static class SecureDataDir
{
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier SystemSid = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public static string Ensure(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        ThrowIfReparseInChain(full);
        Directory.CreateDirectory(full);
        ThrowIfReparseInChain(full);   // created by someone else in between?
        new DirectoryInfo(full).SetAccessControl(RootSecurity());
        SecureChildren(full, ResetChild);
        return full;
    }

    /// <summary>Throws when path or one of its existing parents is a reparse point.</summary>
    internal static void ThrowIfReparseInChain(string path)
    {
        for (var d = Path.GetFullPath(path); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
        {
            if (!Directory.Exists(d) && !File.Exists(d)) continue;
            if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException(
                    $"„{d}“ ist eine Verknüpfung (Junction oder symbolischer Link). Aus Sicherheitsgründen wird dieser Datenordner nicht verwendet. Bitte die Verknüpfung entfernen.");
        }
    }

    /// <summary>
    /// Walks all subfolders without following reparse points: links are removed (only the link,
    /// never its target), every real folder is passed to secure.
    /// </summary>
    internal static void SecureChildren(string root, Action<DirectoryInfo> secure)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            foreach (var entry in dir.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false }))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    RemoveLink(entry);
                    continue;
                }
                if (entry is DirectoryInfo sub)
                {
                    secure(sub);
                    pending.Push(sub);
                }
            }
        }
    }

    // Deleting a junction or symlink removes the link itself; the target stays untouched.
    static void RemoveLink(FileSystemInfo link)
    {
        if (link is DirectoryInfo d) Directory.Delete(d.FullName, recursive: false);
        else File.Delete(link.FullName);
    }

    static DirectorySecurity RootSecurity()
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var sec = new DirectorySecurity();
        sec.SetOwner(Admins);
        sec.SetAccessRuleProtection(true, false); // no inherited rules
        sec.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return sec;
    }

    // Owner Administrators, explicit rules removed, protection off: only the root's rules apply.
    // Fails (and so stops the start) when the folder cannot be taken over.
    static void ResetChild(DirectoryInfo d)
    {
        var sec = d.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var explicitRules = sec.GetAccessRules(true, false, typeof(SecurityIdentifier));
        // already right: skip the write (each write re-propagates inheritance through the subtree)
        if (Admins.Equals(sec.GetOwner(typeof(SecurityIdentifier))) && !sec.AreAccessRulesProtected && explicitRules.Count == 0)
            return;
        sec.SetOwner(Admins);
        foreach (FileSystemAccessRule r in explicitRules)
            sec.RemoveAccessRuleSpecific(r);
        sec.SetAccessRuleProtection(false, false);
        d.SetAccessControl(sec);
    }
}
