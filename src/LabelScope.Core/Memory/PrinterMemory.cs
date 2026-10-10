namespace LabelScope.Core.Memory;

/// <summary>
/// LabelScope's stand-in for a printer's memory (drives R:, E:, B:, A:). Graphics and fonts sent with ~DG, ~DY or
/// ^IS are kept here so a later label can use them with ^XG, ^IM, ^IL or ^A@. A real printer keeps R: until it is
/// switched off; LabelScope keeps everything until it closes or the user clears it. Nothing is saved to disk.
/// Safe to use from several threads: two labels may be drawn at the same time.
/// </summary>
public sealed class PrinterMemory
{
    /// <summary>Default size limit: 64 MB, room for hundreds of full-label backgrounds.</summary>
    public const long DefaultMaxBytes = 64L * 1024 * 1024;

    /// <summary>Default limit on the number of objects.</summary>
    public const int DefaultMaxObjects = 1000;

    // One lock guards both the dictionary and the byte total, so they can never disagree.
    private readonly object _gate = new();
    private readonly Dictionary<string, (ObjectName Name, StoredObject Item)> _items = new(StringComparer.Ordinal);
    private long _bytes;

    /// <summary>Creates an empty memory with the default limits.</summary>
    public PrinterMemory() : this(DefaultMaxBytes, DefaultMaxObjects)
    {
    }

    /// <summary>Creates an empty memory with custom limits (tests use small ones).</summary>
    internal PrinterMemory(long maxBytes, int maxObjects)
    {
        MaxBytes = maxBytes;
        MaxObjects = maxObjects;
    }

    /// <summary>Most bytes kept at once.</summary>
    public long MaxBytes { get; }

    /// <summary>Most objects kept at once.</summary>
    public int MaxObjects { get; }

    /// <summary>
    /// Raised after anything was stored, replaced or deleted. It may be raised on a socket thread; handlers must
    /// not throw (the window's handler only posts to its own thread).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>How many graphics and fonts are kept and how many bytes they use.</summary>
    public MemorySummary Summary
    {
        get
        {
            lock (_gate)
            {
                var fonts = _items.Values.Count(v => v.Item is StoredFont);
                return new MemorySummary(_items.Count - fonts, fonts, _bytes);
            }
        }
    }

    /// <summary>
    /// Stores <paramref name="item"/> under <paramref name="name"/> (on R: when no drive is given), replacing an
    /// object of the same name. Returns null when stored, or a plain-language reason why it was refused.
    /// </summary>
    internal string? Store(ObjectName name, StoredObject item)
    {
        var target = name.Drive is null ? name.OnDrive('R') : name;
        lock (_gate)
        {
            var exists = _items.TryGetValue(target.Key, out var old);
            var oldBytes = exists ? old.Item.SizeInBytes : 0;
            // Refused rather than evicting older objects: a label that silently lost its logo would be worse
            // than a clear message now (plan Decision 1). The old object's size is freed first, so replacing
            // an object in a nearly full memory works.
            if (_bytes - oldBytes + item.SizeInBytes > MaxBytes)
                return $"LabelScope's printer memory is full ({FormatBytes(MaxBytes)}), so {target.Display} was not stored. " +
                       "Delete stored objects with ^ID, or press \"Clear printer memory\" in the window, then send the download again.";
            if (!exists && _items.Count >= MaxObjects)
                return $"LabelScope's printer memory already holds {MaxObjects} objects, so {target.Display} was not stored. " +
                       "Delete stored objects with ^ID, or press \"Clear printer memory\" in the window, then send the download again.";
            _items[target.Key] = (target, item);
            _bytes += item.SizeInBytes - oldBytes;
        }
        // Raised after the lock is released so a handler can read Summary without risk of a deadlock.
        Changed?.Invoke(this, EventArgs.Empty);
        return null;
    }

    /// <summary>
    /// Finds an object. With a drive only that drive is searched, as a printer does; a same-named object on another
    /// drive is reported in <paramref name="elsewhere"/> so the warning can mention it. Without a drive R:, E:, B:,
    /// A: are searched in that order, then any other drive letter a sender used.
    /// </summary>
    internal StoredObject? Find(ObjectName name, out ObjectName? elsewhere)
    {
        elsewhere = null;
        lock (_gate)
        {
            if (name.Drive is not null)
            {
                if (_items.TryGetValue(name.Key, out var hit)) return hit.Item;
                foreach (var (other, _) in _items.Values)
                    if (other.Name == name.Name && other.Extension == name.Extension) { elsewhere = other; break; }
                return null;
            }
            foreach (var drive in ObjectName.SearchOrder)
                if (_items.TryGetValue(name.OnDrive(drive).Key, out var hit)) return hit.Item;
            foreach (var (other, item) in _items.Values)
                if (other.Name == name.Name && other.Extension == name.Extension) return item;
            return null;
        }
    }

    /// <summary>Deletes every object matching <paramref name="pattern"/> (^ID rules, R: when no drive); returns how many.</summary>
    internal int Delete(ObjectName pattern)
    {
        int count;
        lock (_gate)
        {
            var doomed = _items.Where(kv => kv.Value.Name.Matches(pattern)).ToList();
            foreach (var (key, value) in doomed)
            {
                _items.Remove(key);
                _bytes -= value.Item.SizeInBytes;
            }
            count = doomed.Count;
        }
        if (count > 0) Changed?.Invoke(this, EventArgs.Empty);
        return count;
    }

    /// <summary>Empties the memory (the window's "Clear printer memory" button).</summary>
    public void Clear()
    {
        bool any;
        lock (_gate)
        {
            any = _items.Count > 0;
            _items.Clear();
            _bytes = 0;
        }
        if (any) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>"420 KB" or "5 MB": sizes as the window and messages show them.</summary>
    internal static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";
}

/// <summary>What printer memory holds right now.</summary>
/// <param name="Graphics">Number of stored graphics.</param>
/// <param name="Fonts">Number of stored fonts.</param>
/// <param name="TotalBytes">Bytes used.</param>
public sealed record MemorySummary(int Graphics, int Fonts, long TotalBytes)
{
    /// <summary>True when nothing is stored.</summary>
    public bool IsEmpty => Graphics + Fonts == 0;

    /// <summary>Plain text for the status bar: "empty" or "3 graphics, 1 font, 420 KB".</summary>
    public string Describe()
    {
        if (IsEmpty) return "empty";
        var parts = new List<string>();
        if (Graphics > 0) parts.Add(Graphics == 1 ? "1 graphic" : $"{Graphics} graphics");
        if (Fonts > 0) parts.Add(Fonts == 1 ? "1 font" : $"{Fonts} fonts");
        parts.Add(PrinterMemory.FormatBytes(TotalBytes));
        return string.Join(", ", parts);
    }
}
