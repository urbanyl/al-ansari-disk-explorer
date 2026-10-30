namespace AlAnsariDiskExplorer.Models;

public sealed record DriveItem(string RootPath, string Label, long TotalSize, long FreeSpace)
{
    public string Display => string.IsNullOrWhiteSpace(Label)
        ? RootPath.TrimEnd('\\')
        : $"{RootPath.TrimEnd('\\')} ({Label})";

    public string FreeText => $"{SizeFormatter.Format(FreeSpace)} free of {SizeFormatter.Format(TotalSize)}";

    public override string ToString() => Display;
}
