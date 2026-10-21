using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.DynamicDns.Proxy;

/// <summary>
/// Checks at startup that Jellyfin's account can still read and write the plugin's private folders. They are
/// restricted to the account that ran Jellyfin when they were made; if Jellyfin now runs as another one (a
/// Windows service account, a changed Docker user id), its keys, certificates and history cannot be opened,
/// and saying so plainly, with the command that fixes it, beats a stream of unrelated failures.
/// </summary>
internal static class FolderAccess
{
    /// <summary>Gets what is wrong with the folders, with the fix; empty when every one can be used.</summary>
    public static string Problem { get; private set; } = string.Empty;

    /// <summary>
    /// Checks the folders and remembers the first problem found.
    /// </summary>
    /// <param name="folders">The folders to check; ones that do not exist yet are fine.</param>
    /// <returns>The problem, or empty.</returns>
    public static string Check(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        Problem = folders.Select(Describe).FirstOrDefault(p => p.Length > 0) ?? string.Empty;
        return Problem;
    }

    // Opening every file and writing a probe is what the plugin itself will do, so it is what is checked.
    private static string Describe(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return string.Empty;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            }

            var probe = Path.Join(folder, ".access-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, Array.Empty<byte>());
            File.Delete(probe);
            return string.Empty;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"Jellyfin's account, {Account()}, cannot use {folder}: {ex.Message} It was made private to the account that ran Jellyfin before. Fix it with: {FixCommand(folder)}";
        }
    }

    private static string Account()
        => OperatingSystem.IsWindows() ? Environment.UserDomainName + "\\" + Environment.UserName : Environment.UserName;

    private static string FixCommand(string folder)
        => OperatingSystem.IsWindows()
            ? $"icacls \"{folder}\" /grant \"{Account()}:(OI)(CI)F\" /T"
            : $"sudo chown -R {Environment.UserName} \"{folder}\"";
}
