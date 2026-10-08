namespace LabelScope.Core;

/// <summary>
/// Decides how many history entries may be kept. The count limit alone is not enough: 100 labels of a
/// few megabytes each, or a few very large ZPL texts, would still use a lot of memory, so the total size
/// of what is kept is limited as well.
/// </summary>
public static class HistoryBudget
{
    /// <summary>Largest total size, in bytes, of all kept entries (256 MB).</summary>
    public const long DefaultMaxBytes = 256L * 1024 * 1024;

    /// <summary>
    /// Approximate memory used by one entry: its PNG bytes plus its ZPL text. A .NET string uses two bytes
    /// per character, hence the factor of 2.
    /// </summary>
    public static long SizeOf(int pngByteCount, string? zpl) => pngByteCount + 2L * (zpl?.Length ?? 0);

    /// <summary>
    /// Returns how many entries to keep, counting from the newest. The result never exceeds
    /// <paramref name="maxCount"/>, and the kept entries together stay within <paramref name="maxBytes"/>.
    /// The newest entry is always kept, even when it alone is over the budget, so the label that just
    /// arrived is never thrown away before it can be seen.
    /// </summary>
    /// <param name="sizesNewestFirst">The size of every entry, newest first.</param>
    /// <param name="maxCount">The entry count limit (HistoryLimit in settings.json).</param>
    /// <param name="maxBytes">The total size limit.</param>
    public static int EntriesToKeep(IReadOnlyList<long> sizesNewestFirst, int maxCount, long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(sizesNewestFirst);
        var kept = 0;
        long total = 0;
        foreach (var size in sizesNewestFirst)
        {
            if (kept >= maxCount) break;
            if (kept > 0 && total + size > maxBytes) break;
            total += size;
            kept++;
        }
        return kept;
    }
}
