using System.IO;
using System.Runtime.InteropServices;
using AlAnsariDiskExplorer.Models;

namespace AlAnsariDiskExplorer.Services;

public sealed class ScanProgress
{
    public long FilesScanned { get; init; }
    public long BytesScanned { get; init; }
    public long FoldersScanned { get; init; }
    public string CurrentFolder { get; init; } = string.Empty;
    public IReadOnlyList<FileEntry> TopFiles { get; init; } = Array.Empty<FileEntry>();
    public IReadOnlyList<FolderEntry> TopFolders { get; init; } = Array.Empty<FolderEntry>();
}

public sealed class ScanResult
{
    public List<FileEntry> Files { get; set; } = new();
    public List<FolderEntry> Folders { get; set; } = new();
    public long TotalBytes { get; set; }
    public long TotalFiles { get; set; }
    public long TotalFolders { get; set; }
    public int Errors { get; set; }
}

public static class DiskScanner
{
    private const int MaxTopFiles = 1000;
    private const int MaxTopFolders = 300;
    private const int FindExInfoBasic = 1;
    private const int FindFirstExLargeFetch = 2;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindFirstFileExW(
        string fileName,
        int infoLevelId,
        out Win32FindData findFileData,
        int searchOp,
        IntPtr searchFilter,
        int additionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool FindNextFileW(IntPtr findHandle, out Win32FindData findFileData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FindClose(IntPtr findHandle);

    private sealed class DirNode
    {
        public required string Path { get; init; }
        public DirNode? Parent { get; init; }
        public List<DirNode> Children { get; } = new();
        public long OwnSize;
        public long OwnCount;
        public long TotalSize;
        public long TotalCount;
        public FileAttributes Attributes;
    }

    public static ScanResult Scan(string root, bool includeProtected, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        root = Path.GetFullPath(root);
        var result = new ScanResult();
        var nodes = new Dictionary<string, DirNode>(StringComparer.OrdinalIgnoreCase);
        var rootAttributes = FileAttributes.Directory;
        try { rootAttributes = File.GetAttributes(root); } catch { }

        var rootNode = new DirNode { Path = root, Attributes = rootAttributes };
        nodes[root] = rootNode;

        var topFiles = new PriorityQueue<FileEntry, long>();
        var folderSafety = new Dictionary<string, (bool Protected, string Reason)>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<DirNode>();
        stack.Push(rootNode);

        long files = 0;
        long bytes = 0;
        long lastFileReport = 0;
        long lastFolderReport = 0;
        long cancelCheck = 0;

        void Report(string currentFolder)
        {
            if (progress is null) return;

            var now = Environment.TickCount64;
            if (now - lastFileReport < 500) return;

            var includeFolders = now - lastFolderReport >= 1500;
            IReadOnlyList<FolderEntry> topFolders = Array.Empty<FolderEntry>();

            if (includeFolders)
            {
                ComputeTotals(rootNode);
                topFolders = BuildTopFolders(nodes, root, folderSafety, MaxTopFolders);
                lastFolderReport = now;
            }

            progress.Report(new ScanProgress
            {
                FilesScanned = files,
                BytesScanned = bytes,
                FoldersScanned = nodes.Count,
                CurrentFolder = currentFolder,
                TopFiles = SnapshotFiles(topFiles),
                TopFolders = topFolders
            });

            lastFileReport = now;
        }

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var node = stack.Pop();
            var folder = node.Path;
            var pattern = folder.EndsWith(Path.DirectorySeparatorChar) ? folder + "*" : folder + Path.DirectorySeparatorChar + "*";

            var handle = FindFirstFileExW(pattern, FindExInfoBasic, out var data, 0, IntPtr.Zero, FindFirstExLargeFetch);
            if (handle == InvalidHandle)
            {
                var error = Marshal.GetLastWin32Error();
                if (error is not (2 or 18)) result.Errors++;
            }
            else
            {
                try
                {
                    do
                    {
                        if (data.FileName is "." or "..") continue;

                        var attributes = (FileAttributes)data.FileAttributes;

                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if ((attributes & FileAttributes.ReparsePoint) != 0)
                                continue;

                            var childPath = Path.Combine(folder, data.FileName);

                            if (!includeProtected && PathSafety.IsProtected(childPath, attributes, out _))
                                continue;

                            if (!nodes.TryGetValue(childPath, out var child))
                            {
                                child = new DirNode { Path = childPath, Parent = node, Attributes = attributes };
                                nodes[childPath] = child;
                                node.Children.Add(child);
                            }

                            stack.Push(child);
                            continue;
                        }

                        if (++cancelCheck >= 512)
                        {
                            cancelCheck = 0;
                            token.ThrowIfCancellationRequested();
                        }

                        var size = ((long)data.FileSizeHigh << 32) | data.FileSizeLow;
                        var fileTime = ((long)data.LastWriteTimeHigh << 32) | data.LastWriteTimeLow;
                        var modified = fileTime > 0
                            ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime()
                            : DateTime.MinValue;

                        var fullPath = Path.Combine(folder, data.FileName);
                        var isProtected = PathSafety.IsProtected(fullPath, attributes, out var reason);

                        if (!includeProtected && isProtected)
                            continue;

                        files++;
                        bytes += size;
                        node.OwnSize += size;
                        node.OwnCount++;

                        var entry = new FileEntry(fullPath, data.FileName, folder, size, modified, isProtected, reason);

                        if (topFiles.Count < MaxTopFiles)
                        {
                            topFiles.Enqueue(entry, size);
                        }
                        else if (topFiles.TryPeek(out _, out var smallest) && size > smallest)
                        {
                            topFiles.Dequeue();
                            topFiles.Enqueue(entry, size);
                        }

                        Report(folder);
                    }
                    while (FindNextFileW(handle, out data));
                }
                finally
                {
                    FindClose(handle);
                }
            }

            Report(folder);
        }

        result.TotalFiles = files;
        result.TotalBytes = bytes;
        result.TotalFolders = nodes.Count;
        result.Files = SnapshotFiles(topFiles);

        ComputeTotals(rootNode);
        result.Folders = BuildTopFolders(nodes, root, folderSafety, MaxTopFolders);

        return result;
    }

    private static List<FileEntry> SnapshotFiles(PriorityQueue<FileEntry, long> queue)
    {
        return queue.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(file => file.Size)
            .ToList();
    }

    private static void ComputeTotals(DirNode node)
    {
        var size = node.OwnSize;
        var count = node.OwnCount;

        foreach (var child in node.Children)
        {
            ComputeTotals(child);
            size += child.TotalSize;
            count += child.TotalCount;
        }

        node.TotalSize = size;
        node.TotalCount = count;
    }

    private static List<FolderEntry> BuildTopFolders(
        Dictionary<string, DirNode> nodes,
        string root,
        Dictionary<string, (bool Protected, string Reason)> safetyCache,
        int max)
    {
        var top = new PriorityQueue<FolderEntry, long>();

        foreach (var node in nodes.Values)
        {
            if (SamePath(node.Path, root)) continue;

            if (top.Count >= max && top.TryPeek(out _, out var smallest) && node.TotalSize <= smallest)
                continue;

            if (!safetyCache.TryGetValue(node.Path, out var safety))
            {
                var isProtected = PathSafety.IsProtected(node.Path, node.Attributes, out var reason);
                safety = (isProtected, reason);
                safetyCache[node.Path] = safety;
            }

            var entry = new FolderEntry(
                node.Path,
                GetFolderName(node.Path),
                node.TotalSize,
                node.TotalCount,
                safety.Protected,
                safety.Reason);

            if (top.Count < max)
            {
                top.Enqueue(entry, entry.Size);
            }
            else
            {
                top.Dequeue();
                top.Enqueue(entry, entry.Size);
            }
        }

        return top.UnorderedItems
            .Select(item => item.Element)
            .OrderByDescending(folder => folder.Size)
            .ToList();
    }

    private static string GetFolderName(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}



