using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Writes private keys and certificates, and keeps the folders holding them private. Each file is readable only
/// by the account Jellyfin runs as (and, on Windows, SYSTEM and Administrators) from the moment it exists,
/// flushed to disk before it replaces the old one, and written under its own temporary name, so a reader never
/// sees half a file and two writers never share one.
/// </summary>
internal static class SecretFile
{
    /// <summary>
    /// Replaces a file with new contents.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="contents">The new contents.</param>
    public static void Write(string path, ReadOnlySpan<byte> contents)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = Create(temp))
            {
                file.Write(contents);
                file.Flush(flushToDisk: true);
            }

            // A rename on the same volume keeps the file's own permissions.
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Creates a folder, or narrows an existing one, so only the account Jellyfin runs as can open it. On Windows,
    /// SYSTEM and Administrators keep access, and files created inside inherit the same rights.
    /// </summary>
    /// <param name="path">The folder.</param>
    public static void ProtectFolder(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsWindows())
        {
            new DirectoryInfo(path).SetAccessControl(FolderSecurity());
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // Created private, rather than created readable and narrowed afterwards.
    private static FileStream Create(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, FileSecurityFor());
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Options = FileOptions.WriteThrough,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        };
        return new FileStream(path, options);
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity FileSecurityFor()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners())
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity FolderSecurity()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in Owners())
        {
            security.AddAccessRule(new FileSystemAccessRule(
                sid,
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    // The account Jellyfin runs as, and the two the system itself needs: SYSTEM for services and backups,
    // Administrators so an administrator can still recover the files.
    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] Owners()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new[]
        {
            identity.User ?? new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
    }
}
