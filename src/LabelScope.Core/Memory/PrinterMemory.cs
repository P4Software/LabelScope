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

    // ^CW assignments: font letter -> font file, also guarded by _gate. At most 36 entries (A to Z, 0 to 9), so the
    // map needs no limit of its own. Kept for the session like stored objects, as a printer keeps them until it is
    // switched off, so a ^CW sent as a job of its own still works for the labels that follow.
    private readonly Dictionary<char, ObjectName> _fontIds = new();

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

    /// <summary>Label setup (^PW, ^LL, ^LH, ^PO, ^LR) kept between jobs; cleared together with the memory.</summary>
    public PrinterSetup Setup { get; } = new();

    /// <summary>Most bytes kept at once.</summary>
    public long MaxBytes { get; }

    /// <summary>Most objects kept at once.</summary>
    public int MaxObjects { get; }

    /// <summary>
    /// Raised after anything was stored, replaced or deleted. It may be raised on a socket thread; handlers must
    /// not throw (the window's handler only posts to its own thread).
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>How many graphics, fonts and ^CW font letters are kept and how many bytes the graphics and fonts use.</summary>
    public MemorySummary Summary
    {
        get
        {
            // One lock for all counts, so the summary is a consistent snapshot even while another job stores.
            lock (_gate)
            {
                var fonts = _items.Values.Count(v => v.Item is StoredFont);
                return new MemorySummary(_items.Count - fonts, fonts, _bytes, _fontIds.Count);
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
            // than a clear message now. The old object's size is freed first, so replacing
            // an object in a nearly full memory works.
            // An object bigger than the whole memory can never fit, so "clear memory" would be a false hint.
            if (item.SizeInBytes > MaxBytes)
                return Text.Get("Memory_ObjectTooLarge_" + item.Kind, FormatBytes(item.SizeInBytes), FormatBytes(MaxBytes));
            if (_bytes - oldBytes + item.SizeInBytes > MaxBytes)
                return Text.Get("Memory_Full", FormatBytes(MaxBytes), target.Display);
            if (!exists && _items.Count >= MaxObjects)
                return Text.Get(MaxObjects == 1 ? "Memory_FullCount_One" : "Memory_FullCount_Many", MaxObjects, target.Display);
            _items[target.Key] = (target, item);
            _bytes += item.SizeInBytes - oldBytes;
        }
        // Raised after the lock is released so a handler can read Summary without risk of a deadlock.
        RaiseChanged();
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
                elsewhere = FirstOnAnyDrive(name)?.Name;
                return null;
            }
            foreach (var drive in ObjectName.SearchOrder)
                if (_items.TryGetValue(name.OnDrive(drive).Key, out var hit)) return hit.Item;
            return FirstOnAnyDrive(name)?.Item;
        }
    }

    /// <summary>
    /// The object with this name and extension on any drive, preferring R, E, B, A and then other letters
    /// alphabetically, so the result never depends on dictionary order. Caller holds the lock.
    /// </summary>
    private (ObjectName Name, StoredObject Item)? FirstOnAnyDrive(ObjectName name)
    {
        (ObjectName Name, StoredObject Item)? best = null;
        var bestRank = int.MaxValue;
        foreach (var entry in _items.Values)
        {
            if (entry.Name.Name != name.Name || entry.Name.Extension != name.Extension) continue;
            var drive = entry.Name.Drive ?? 'R';
            var index = Array.IndexOf(ObjectName.SearchOrder, drive);
            var rank = index >= 0 ? index : 100 + drive;
            if (rank < bestRank) { best = entry; bestRank = rank; }
        }
        return best;
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
        if (count > 0) RaiseChanged();
        return count;
    }

    /// <summary>^CW: from now on font letter <paramref name="id"/> means the font file <paramref name="file"/>.</summary>
    internal void AssignFont(char id, ObjectName file)
    {
        lock (_gate) _fontIds[char.ToUpperInvariant(id)] = file;
        // A font letter is part of what memory holds (the window counts it and "Clear printer memory" forgets it),
        // so listeners hear about it like any other change. Raised after the lock, as in Store.
        RaiseChanged();
    }

    /// <summary>The font file a ^CW gave letter <paramref name="id"/>, or null.</summary>
    internal ObjectName? FontFor(char id)
    {
        lock (_gate) return _fontIds.TryGetValue(char.ToUpperInvariant(id), out var file) ? file : null;
    }

    /// <summary>Empties the memory and forgets every ^CW font letter and the label setup (the window's "Clear printer memory" button).</summary>
    public void Clear()
    {
        Setup.Reset();
        bool any;
        lock (_gate)
        {
            any = _items.Count > 0 || _fontIds.Count > 0;
            _items.Clear();
            _bytes = 0;
            _fontIds.Clear();
        }
        if (any) RaiseChanged();
    }

    /// <summary>
    /// Calls every <see cref="Changed"/> handler on its own, so one broken handler (for example a UI handler that
    /// throws) can neither fail a store, delete or clear that already succeeded nor stop the handlers after it.
    /// </summary>
    private void RaiseChanged()
    {
        var handler = Changed;
        if (handler is null) return;
        foreach (var h in handler.GetInvocationList())
        {
            try { ((EventHandler)h)(this, EventArgs.Empty); }
            catch { /* The change is done; a failing listener must not turn it into an error. */ }
        }
    }

    /// <summary>"420 KB" or "5 MB": sizes as the window and messages show them.</summary>
    internal static string FormatBytes(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : $"{Math.Max(1, (bytes + 1023) / 1024)} KB";
}

/// <summary>What printer memory holds right now.</summary>
/// <param name="Graphics">Number of stored graphics.</param>
/// <param name="Fonts">Number of stored fonts.</param>
/// <param name="TotalBytes">Bytes used by the stored graphics and fonts.</param>
/// <param name="FontLetters">Number of font letters given a font file with ^CW (they use no bytes of their own).</param>
public sealed record MemorySummary(int Graphics, int Fonts, long TotalBytes, int FontLetters = 0)
{
    /// <summary>True when nothing is stored and no font letter is assigned.</summary>
    public bool IsEmpty => Graphics + Fonts + FontLetters == 0;

    /// <summary>Plain text for the status bar: "empty", "2 font letters" or "3 graphics, 1 font, 420 KB, 2 font letters".</summary>
    public string Describe()
    {
        if (IsEmpty) return Text.Get("Memory_Empty");
        var parts = new List<string>();
        if (Graphics > 0) parts.Add(Graphics == 1 ? Text.Get("Memory_OneGraphic") : Text.Get("Memory_Graphics", Graphics));
        if (Fonts > 0) parts.Add(Fonts == 1 ? Text.Get("Memory_OneFont") : Text.Get("Memory_Fonts", Fonts));
        // The size belongs to the stored objects only: with nothing but font letters it would read "1 KB" (the
        // smallest size FormatBytes shows), which is not true.
        if (Graphics + Fonts > 0) parts.Add(PrinterMemory.FormatBytes(TotalBytes));
        if (FontLetters > 0) parts.Add(FontLetters == 1 ? Text.Get("Memory_OneFontLetter") : Text.Get("Memory_FontLetters", FontLetters));
        return string.Join(", ", parts);
    }
}
