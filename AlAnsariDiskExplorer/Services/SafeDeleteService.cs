using System.IO;
using System.Runtime.InteropServices;
using AlAnsariDiskExplorer.Models;

namespace AlAnsariDiskExplorer.Services;

public sealed class DeleteOutcome
{
    public int Deleted { get; set; }
    public long BytesFreed { get; set; }
    public List<string> Errors { get; } = new();
    public List<FileEntry> DeletedFiles { get; } = new();
}

public static class SafeDeleteService
{
    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT fileOp);

    public static bool IsDeletable(FileEntry file)
        => !file.IsProtected && !PathSafety.IsProtected(file.FullPath, out _);

    public static DeleteOutcome Delete(IEnumerable<FileEntry> files, bool permanent, DeletionLog log)
    {
        var outcome = new DeleteOutcome();
        var allowed = new List<FileEntry>();

        foreach (var file in files)
        {
            if (!IsDeletable(file))
            {
                outcome.Errors.Add($"{file.FullPath}: blocked by the protected-file policy.");
                continue;
            }

            if (!File.Exists(file.FullPath))
            {
                outcome.Errors.Add($"{file.FullPath}: file not found.");
                continue;
            }

            allowed.Add(file);
        }

        if (allowed.Count == 0) return outcome;

        if (permanent)
        {
            foreach (var file in allowed)
            {
                try
                {
                    File.Delete(file.FullPath);
                    outcome.Deleted++;
                    outcome.BytesFreed += file.Size;
                    outcome.DeletedFiles.Add(file);
                }
                catch (Exception ex)
                {
                    outcome.Errors.Add($"{file.Name}: {ex.Message}");
                }
            }
        }
        else
        {
            if (Recycle(allowed.Select(f => f.FullPath), out var error))
            {
                outcome.Deleted = allowed.Count;
                outcome.BytesFreed = allowed.Sum(f => f.Size);
                outcome.DeletedFiles.AddRange(allowed);
            }
            else
            {
                outcome.Errors.Add(error);
            }
        }

        if (outcome.DeletedFiles.Count > 0)
            log.Append(outcome.DeletedFiles.Select(f => (f.FullPath, f.Size)), permanent);

        return outcome;
    }

    private static bool Recycle(IEnumerable<string> paths, out string error)
    {
        error = string.Empty;

        try
        {
            var from = string.Join("\0", paths) + "\0\0";
            var operation = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = from,
                fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI)
            };

            var code = SHFileOperation(ref operation);
            if (code != 0)
            {
                error = $"The Recycle Bin operation failed (code {code}).";
                return false;
            }

            if (operation.fAnyOperationsAborted)
            {
                error = "The deletion was aborted.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
