using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Cabinet.Core.Utilities;

/// <summary>
/// Keeps a folder Cabinet writes its data to readable only by the server: on Windows, SYSTEM,
/// Administrators and the account it runs as. Folders under C:\ProgramData or C:\Program Files
/// otherwise inherit read access for every user on the machine, which would expose events, key
/// hashes, the user directory and the keys that sign dashboard sessions. On Linux the services'
/// umask and data folder do this already.
/// </summary>
public static class PrivateFolder
{
    /// <summary>
    /// Makes the folder private if anyone besides the server can reach it. Returns whether it
    /// changed anything. Errors are the caller's to log: a folder left open shouldn't stop the server.
    /// </summary>
    public static bool Secure(string path)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(path))
        {
            return false;
        }

        return SecureOnWindows(path);
    }

    /// <summary>Whether anyone besides SYSTEM, Administrators and this account has access.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsOpen(string path)
    {
        var allowed = Allowed();
        return new DirectoryInfo(path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow && !allowed.Contains((SecurityIdentifier)rule.IdentityReference));
    }

    [SupportedOSPlatform("windows")]
    private static bool SecureOnWindows(string path)
    {
        if (!IsOpen(path))
        {
            return false;
        }

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var who in Allowed())
        {
            security.AddAccessRule(new FileSystemAccessRule(who, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(security);
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static HashSet<SecurityIdentifier> Allowed()
    {
        var allowed = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
        };

        if (WindowsIdentity.GetCurrent().User is { } self)
        {
            allowed.Add(self);
        }

        return allowed;
    }
}
