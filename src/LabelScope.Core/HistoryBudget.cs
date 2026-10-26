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

    /// <summary>
    /// Like <see cref="EntriesToKeep"/>, for a list where some entries do not count toward the count limit (sample
    /// labels, which must never push out a real job). Walks from the newest entry: an entry that counts is dropped once
    /// <paramref name="maxCount"/> counting entries are kept (older entries that do not count may still be kept); the
    /// byte budget covers every entry, and the first entry that would go over it is dropped together with everything
    /// older. The first entry kept is kept even when it alone is over the budget, so the label that just arrived is
    /// never thrown away before it can be seen.
    /// </summary>
    /// <param name="sizesNewestFirst">The size of every entry, newest first.</param>
    /// <param name="countsTowardLimit">For each entry, in the same order: true when it counts toward <paramref name="maxCount"/>.</param>
    /// <param name="maxCount">The count limit for the entries that count (HistoryLimit in settings.json).</param>
    /// <param name="maxBytes">The total size limit, for all entries.</param>
    /// <returns>For each entry, in the same order: true to keep it.</returns>
    public static bool[] KeepFlags(IReadOnlyList<long> sizesNewestFirst, IReadOnlyList<bool> countsTowardLimit, int maxCount,
        long maxBytes = DefaultMaxBytes)
    {
        ArgumentNullException.ThrowIfNull(sizesNewestFirst);
        ArgumentNullException.ThrowIfNull(countsTowardLimit);
        if (sizesNewestFirst.Count != countsTowardLimit.Count)
            throw new ArgumentException("There must be one size for each entry.", nameof(countsTowardLimit));

        var flags = new bool[sizesNewestFirst.Count];
        var counted = 0;
        var keptAny = false;
        long total = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            // Over the count limit: this one goes, but it adds no bytes, so older exempt entries are still judged.
            if (countsTowardLimit[i] && counted >= maxCount) continue;
            // Over the byte budget: everything from here on is older and goes too.
            if (keptAny && total + sizesNewestFirst[i] > maxBytes) break;
            total += sizesNewestFirst[i];
            keptAny = true;
            flags[i] = true;
            if (countsTowardLimit[i]) counted++;
        }
        return flags;
    }
}
