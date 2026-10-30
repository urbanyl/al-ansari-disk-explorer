using System.Globalization;

namespace AlAnsariDiskExplorer.Models;

public static class SizeFormatter
{
    public static string Format(long bytes)
    {
        if (bytes < 0) return "0 B";
        if (bytes < 1024) return $"{bytes} B";

        var kb = bytes / 1024.0;
        if (kb < 1024) return $"{kb:0.#} KB";

        var mb = kb / 1024.0;
        if (mb < 1024) return $"{mb:0.#} MB";

        var gb = mb / 1024.0;
        if (gb < 1024) return $"{gb:0.##} GB";

        return $"{gb / 1024.0:0.##} TB";
    }

    public static string FormatInvariant(long bytes) => Format(bytes).Replace(',', '.');
}
