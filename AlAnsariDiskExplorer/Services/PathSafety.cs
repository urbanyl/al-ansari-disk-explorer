using System.IO;

namespace AlAnsariDiskExplorer.Services;

public static class PathSafety
{
    private static readonly HashSet<string> ProtectedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData",
        "$Recycle.Bin", "System Volume Information", "Recovery", "Boot", "EFI", "PerfLogs"
    };

    private static readonly HashSet<string> ProtectedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "bootnxt", "bcd", "ntldr",
        "ntuser.dat", "ntuser.ini", "desktop.ini", "boot.ini", "bootfont.bin",
        "dumpstack.log.tmp", "dumpstack.log"
    };

    public static bool IsProtected(string fullPath, out string reason)
    {
        FileAttributes attributes = 0;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch
        {
            // Attributes unavailable: rely on the path rules.
        }

        return IsProtected(fullPath, attributes, out reason);
    }

    public static bool IsProtected(string fullPath, FileAttributes attributes, out string reason)
    {
        reason = string.Empty;

        try
        {
            var path = Path.GetFullPath(fullPath);
            var root = Path.GetPathRoot(path) ?? string.Empty;
            var relative = path.Length > root.Length ? path[root.Length..] : string.Empty;
            var parts = relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length > 0 && ProtectedFolders.Contains(parts[0]))
            {
                reason = "Windows or installed program";
                return true;
            }

            if (parts.Length > 2 &&
                parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase) &&
                parts.Any(p => p.Equals("AppData", StringComparison.OrdinalIgnoreCase)))
            {
                reason = "Application data";
                return true;
            }

            var name = Path.GetFileName(path);
            if (ProtectedNames.Contains(name))
            {
                reason = "System file";
                return true;
            }

            if (parts.Length == 1 && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase))
            {
                reason = "Kernel or driver file";
                return true;
            }

            if ((attributes & FileAttributes.System) != 0)
            {
                reason = "System attribute";
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }
}
