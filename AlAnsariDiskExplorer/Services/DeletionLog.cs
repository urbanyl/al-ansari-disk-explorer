using System.IO;

namespace AlAnsariDiskExplorer.Services;

public sealed class DeletionLog
{
    public DeletionLog()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Al-Ansari",
            "DiskExplorer");
        LogFile = Path.Combine(DirectoryPath, "deletions.log");
    }

    public string DirectoryPath { get; }
    public string LogFile { get; }

    public void Append(IEnumerable<(string Path, long Size)> files, bool permanent)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var mode = permanent ? "PERMANENT" : "RECYCLE";

            foreach (var file in files)
            {
                File.AppendAllText(
                    LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {mode} {file.Path} ({Models.SizeFormatter.Format(file.Size)}){Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort.
        }
    }

    public string Read(int maxLines = 500)
    {
        try
        {
            if (!File.Exists(LogFile)) return string.Empty;
            return string.Join(Environment.NewLine, File.ReadAllLines(LogFile).TakeLast(maxLines));
        }
        catch
        {
            return string.Empty;
        }
    }
}
