namespace AlAnsariDiskExplorer.Models;

public sealed record FolderEntry(
    string FullPath,
    string Name,
    long Size,
    long FileCount,
    bool IsProtected,
    string ProtectReason)
{
    public string SizeText => SizeFormatter.Format(Size);
    public string FileCountText => FileCount.ToString("N0");
    public string StatusText => IsProtected ? "Protected" : "Deletable";
    public string ToolTip => IsProtected ? $"Protected: {ProtectReason}" : "Folder (files only can be deleted)";
}
