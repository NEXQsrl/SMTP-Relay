using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmtpRelay.Security;

/// <summary>
/// Hardens the NTFS permissions of sensitive files and folders: SYSTEM and Administrators only,
/// no inheritance from the parent. Well-known SIDs are used so that it works in any Windows language.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsAcl
{
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>True when the process runs elevated as an administrator, or as LocalSystem.</summary>
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static void RestrictToSystemAndAdmins(string filePath)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, AccessControlType.Allow));
        AddCurrentUserIfNeeded(security);
        new FileInfo(filePath).SetAccessControl(security);
    }

    /// <summary>
    /// Restricts the data folder (and, through inheritance, everything in it: logs, spool, secrets) to SYSTEM,
    /// Administrators and, when given, the account the service runs as. By default any local user can read and
    /// create files under C:\ProgramData, which would expose mail content and let a user plant files.
    /// </summary>
    /// <param name="directoryPath">Folder to protect. It is created when missing.</param>
    /// <param name="serviceAccount">Account of the service when it is not LocalSystem (for example "DOMAIN\svc-relay").</param>
    public static void HardenDataDirectory(string directoryPath, string? serviceAccount = null)
    {
        Directory.CreateDirectory(directoryPath);

        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));

        var serviceSid = ResolveAccount(serviceAccount);
        if (serviceSid is not null && serviceSid != LocalSystem)
            security.AddAccessRule(new FileSystemAccessRule(serviceSid, FileSystemRights.Modify, inherit, PropagationFlags.None, AccessControlType.Allow));

        new DirectoryInfo(directoryPath).SetAccessControl(security);
    }

    /// <summary>Resolves an account name to a SID. Returns null for LocalSystem, an empty name or an unknown account.</summary>
    public static SecurityIdentifier? ResolveAccount(string? account)
    {
        if (string.IsNullOrWhiteSpace(account))
            return null;

        var name = account.Trim();
        if (name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase))
            return null;
        if (name.StartsWith(@".\", StringComparison.Ordinal))
            name = Environment.MachineName + name[1..];

        try { return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier)); }
        catch (IdentityNotMappedException) { return null; }
        catch (SystemException) { return null; }
    }

    // A file created by a non-elevated user (development, console mode) must stay readable by that user.
    private static void AddCurrentUserIfNeeded(FileSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is null || identity.IsSystem)
            return;
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            return;
        security.AddAccessRule(new FileSystemAccessRule(identity.User, FileSystemRights.FullControl, AccessControlType.Allow));
    }
}
