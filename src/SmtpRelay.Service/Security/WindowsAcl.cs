using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmtpRelay.Security;

/// <summary>
/// Irrobustisce i permessi NTFS di file/cartelle sensibili: solo SYSTEM e Administrators,
/// nessuna ereditarieta. Usa i SID well-known per essere indipendente dalla lingua del SO.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    private static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void RestrictToSystemAndAdmins(string filePath)
    {
        var fi = new FileInfo(filePath);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        try { security.SetOwner(Admins); } catch { /* richiede privilegi; non bloccante */ }
        fi.SetAccessControl(security);
    }

    public static void RestrictDirectoryToSystemAndAdmins(string directoryPath)
    {
        var di = new DirectoryInfo(directoryPath);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        try { security.SetOwner(Admins); } catch { /* non bloccante */ }
        di.SetAccessControl(security);
    }
}
