using System.IO;

namespace AlAnsariDiskExplorer.Models;

public sealed record FileEntry(
    string FullPath,
    string Name,
    string Folder,
    long Size,
    DateTime LastWriteTime,
    bool IsProtected,
    string ProtectReason)
{
    public string SizeText => SizeFormatter.Format(Size);
    public string ModifiedText => LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    public string StatusText => IsProtected ? "Protected" : "Deletable";
    public string TypeText => Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();
    public string ToolTip => IsProtected ? $"Protected: {ProtectReason}" : "Safe to delete";
}
