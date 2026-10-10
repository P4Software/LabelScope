using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security;
using LabelScope.Core.Memory;
using SkiaSharp;

namespace LabelScope.Core.Fonts;

/// <summary>
/// The TrueType and OpenType fonts in the folder named by the FontsFolder setting. The folder is listed once at
/// start-up; a font named in a label (^A@, ^CW) is then looked up by file name only, so a name in a label can never
/// reach a file outside that folder, however it is written. Changes to the folder or the setting take effect when
/// LabelScope is restarted. Safe to use from several threads: two labels may be drawn at the same time.
/// </summary>
public sealed class FontLibrary
{
    /// <summary>Font files larger than this are skipped (no real label font comes close).</summary>
    public const long MaxFontFileBytes = 32L * 1024 * 1024;

    /// <summary>At most this many font files are listed.</summary>
    public const int MaxFiles = 500;

    /// <summary>
    /// Most bytes of folder fonts held in memory at once. Without it one label from the network naming every listed
    /// font could pin 500 x 32 MB. When it is spent, further fonts are refused, not swapped: a typeface already handed
    /// to a field has no reference count, so unloading one could pull it away from a label being drawn.
    /// </summary>
    public const long MaxLoadedBytes = 256L * 1024 * 1024;

    // Windows device names: a file such as CON.TTF opens a device, not a file, on older Windows. Never listed or looked
    // up. Windows also reserves COM and LPT with 0 and with the superscript digits 1, 2 and 3, and the console names.
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    // Separators, the drive or stream colon, and NUL, for Windows and Unix alike.
    private static readonly char[] PathCharacters = ['\\', '/', ':', '\0'];

    // "ARIAL.TTF" -> full path. Filled once by FromFolder and never changed, so reading it needs no lock. Every key is
    // a bare file name taken from the folder listing; a ZPL name is only ever compared against these keys.
    private readonly Dictionary<string, string> _files;

    // Fonts that were loaded, or that Skia could not parse, by full path. Only results that cannot change are kept:
    // a file that could not be opened (locked by a virus scanner, for example) or did not fit the budget is tried
    // again on the next use. The keys can only come from _files, so there are never more than MaxFiles entries.
    private readonly ConcurrentDictionary<string, LoadedFont> _loaded = new(StringComparer.Ordinal);

    // Serialises loading, so a file is read once even when two renders ask at the same time, and so the budget
    // check and the reservation below are one step. Loads are rare (once per font per session); look-ups of
    // fonts already loaded never take this lock.
    private readonly object _loadGate = new();

    // Bytes of loaded fonts, counted against LoadedBytesLimit. Only changed under _loadGate; read with Interlocked so
    // a reader on another thread always sees a whole value.
    private long _loadedBytes;

    private FontLibrary(Dictionary<string, string> files, long loadedBytesLimit)
    {
        _files = files;
        LoadedBytesLimit = loadedBytesLimit;
    }

    /// <summary>No folder: only the built-in fonts.</summary>
    public static FontLibrary Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal), MaxLoadedBytes);

    /// <summary>Number of font files found.</summary>
    public int Count => _files.Count;

    /// <summary>Most bytes of fonts this library holds at once (<see cref="MaxLoadedBytes"/>; tests use less).</summary>
    internal long LoadedBytesLimit { get; }

    /// <summary>Bytes of fonts loaded so far.</summary>
    internal long LoadedBytes => Interlocked.Read(ref _loadedBytes);

    /// <summary>
    /// Turns font bytes into a typeface: Skia's parser. Tests replace it to simulate a failure no one expected (a bug in
    /// a parser), which must still come back as a plain warning.
    /// </summary>
    internal Func<SKData, SKTypeface?> TypefaceFactory { get; set; } = data => SKTypeface.FromData(data);

    /// <summary>
    /// Sets aside native memory for a font file of the given length: Skia's allocator. Tests replace it to simulate
    /// memory running out, which Skia reports with no buffer (null or a zero pointer) instead of an exception.
    /// </summary>
    internal Func<long, SKData?> DataFactory { get; set; } = length => SKData.Create(length);

    /// <summary>
    /// Lists the fonts in <paramref name="folder"/> (relative to <paramref name="programFolder"/> when not rooted).
    /// Problems are added to <paramref name="messages"/> in plain language; the result is never null and this method
    /// never throws for a bad setting or an unreadable folder. The setting is only read, never written back.
    /// </summary>
    public static FontLibrary FromFolder(string? folder, string programFolder, List<string> messages) =>
        FromFolder(folder, programFolder, messages, MaxLoadedBytes);

    /// <summary>As the public overload, with a custom budget for loaded fonts (tests use a small one).</summary>
    internal static FontLibrary FromFolder(string? folder, string programFolder, List<string> messages, long loadedBytesLimit)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Empty;
        ArgumentNullException.ThrowIfNull(messages);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var tooBig = 0;
        var tooLong = 0;
        try
        {
            var path = Path.IsPathRooted(folder) ? folder : Path.Combine(programFolder, folder);
            if (!Directory.Exists(path))
            {
                messages.Add(File.Exists(path)
                    ? $"The FontsFolder in settings.json ({folder}) is a file, not a folder, so only LabelScope's built-in fonts are used. Name the folder that holds your fonts, then restart LabelScope."
                    : $"The FontsFolder in settings.json ({folder}) does not exist, so only LabelScope's built-in fonts are used. Create the folder or correct the setting, then restart LabelScope.");
                return Empty;
            }

            var seen = 0;
            // Top level only: the user named this one folder, and fonts in its subfolders were not offered.
            foreach (var file in new DirectoryInfo(path).EnumerateFiles())
            {
                if (file.Extension.ToUpperInvariant() is not (".TTF" or ".OTF")) continue;
                // Links (symbolic links, junction-like reparse points) could point anywhere on the disk; only real
                // files that sit in the folder itself are offered to labels. Device names never name a real file.
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if (IsDeviceName(file.Name)) continue;
                if (++seen > MaxFiles)
                {
                    messages.Add($"The FontsFolder holds more than {MaxFiles} font files; only the first {MaxFiles} are used. Move the fonts you do not need to another folder, then restart LabelScope.");
                    break;
                }
                // A ZPL name keeps at most 64 characters (ObjectName.MaxNameLength), so a longer file name could
                // never be named by a label; listing it would only hide that.
                if (Path.GetFileNameWithoutExtension(file.Name).Length > ObjectName.MaxNameLength) { tooLong++; continue; }
                long length;
                try { length = file.Length; }
                catch (IOException) { continue; }   // removed while the folder was being listed: nothing to offer
                if (length > MaxFontFileBytes) { tooBig++; continue; }
                files.TryAdd(file.Name.ToUpperInvariant(), file.FullName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
        {
            messages.Add($"LabelScope could not read the FontsFolder ({ex.Message}), so only its built-in fonts are used. Check that the folder can be opened, then restart LabelScope.");
            return new FontLibrary(files, loadedBytesLimit);
        }
        if (tooBig > 0)
            messages.Add(tooBig == 1
                ? "1 font file in the FontsFolder is larger than 32 MB and was skipped. No label font is that large: check that it really is a font, then restart LabelScope."
                : $"{tooBig} font files in the FontsFolder are larger than 32 MB and were skipped. No label font is that large: check that they really are fonts, then restart LabelScope.");
        if (tooLong > 0)
            messages.Add(tooLong == 1
                ? $"1 font file in the FontsFolder has a name longer than {ObjectName.MaxNameLength} characters, so a label cannot name it. Give it a shorter name, then restart LabelScope."
                : $"{tooLong} font files in the FontsFolder have names longer than {ObjectName.MaxNameLength} characters, so a label cannot name them. Give them shorter names, then restart LabelScope.");
        return new FontLibrary(files, loadedBytesLimit);
    }

    /// <summary>
    /// The font whose file name is <paramref name="fileName"/> (for example ARIAL.TTF, any case), loaded once and kept.
    /// When null, <paramref name="problem"/> says why: not listed (<see cref="FontProblem.None"/>), not a font Skia can
    /// read, not openable right now, or over the memory budget. Anything that is not a plain file name (a drive, a
    /// folder, "..", a stream, a device name) finds nothing.
    /// </summary>
    internal SKTypeface? Find(string fileName, out FontProblem problem)
    {
        problem = FontProblem.None;
        if (!IsPlainFileName(fileName)) return null;
        if (!_files.TryGetValue(fileName.ToUpperInvariant(), out var path)) return null;

        if (!_loaded.TryGetValue(path, out var font))
        {
            lock (_loadGate)
            {
                if (!_loaded.TryGetValue(path, out font))
                {
                    try
                    {
                        font = Load(path);
                    }
                    catch (OutOfMemoryException)
                    {
                        // Like an IO failure, this may pass (other labels free their memory), so it is not remembered:
                        // the font is tried again on its next use. Load has already given back the bytes it reserved.
                        font = new LoadedFont(null, null, FontProblem.Unopenable);
                    }
                    catch (Exception)
                    {
                        // Load turns every failure it knows into a FontProblem. Anything else (a bug in a font parser,
                        // for example) must not reach the renderer either: a label names this font from the network,
                        // and the text still has to be drawn with the stand-in font. Load has already given back the
                        // bytes it reserved. Remembered as unreadable, so the same file does not fail again and again.
                        font = new LoadedFont(null, null, FontProblem.Unreadable);
                    }
                    if (font.Problem is FontProblem.None or FontProblem.Unreadable) _loaded[path] = font;
                }
            }
        }
        problem = font.Problem;
        return font.Typeface;
    }

    /// <summary>
    /// Defence in depth: the listing only holds bare names, so a path could never match anyway. Refusing path
    /// characters, "..", a trailing dot or space (Windows drops them, so "A.TTF." opens A.TTF) and device names here
    /// keeps that true even if the listing ever changes.
    /// </summary>
    internal static bool IsPlainFileName(string name) =>
        name.Length > 0 &&
        name.IndexOfAny(PathCharacters) < 0 &&
        !name.Contains("..", StringComparison.Ordinal) &&
        !name.EndsWith('.') && !name.EndsWith(' ') &&
        !IsDeviceName(name);

    /// <summary>
    /// True when any dot-separated part of <paramref name="name"/> is a Windows device name. Windows reads the part
    /// before the first dot as the device (COM1.X.TTF is the COM1 port) and drops spaces before a dot ("CON .TTF"
    /// is CON); checking every part, not only the first, also refuses names a future Windows might read differently.
    /// </summary>
    private static bool IsDeviceName(string name)
    {
        foreach (var part in name.Split('.'))
            if (DeviceNames.Contains(part.TrimEnd(' '))) return true;
        return false;
    }

    /// <summary>
    /// Reads the file straight into Skia's memory (one copy, and the file is closed again, so the user can replace or
    /// delete it) and parses it. Never throws: every failure is a <see cref="FontProblem"/>. Caller holds _loadGate.
    /// </summary>
    private LoadedFont Load(string path)
    {
        long reserved = 0;
        var loaded = false;
        try
        {
            // The listing may have been changed since start-up: a file swapped for a link could point anywhere.
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                return new LoadedFont(null, null, FontProblem.Unopenable);

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            // Checked again here, because the file may have grown since the folder was listed.
            if (length == 0 || length > MaxFontFileBytes) return new LoadedFont(null, null, FontProblem.Unreadable);
            if (LoadedBytes + length > LoadedBytesLimit) return new LoadedFont(null, null, FontProblem.OverBudget);
            Interlocked.Add(ref _loadedBytes, length);
            reserved = length;

            var data = ReadInto(stream, length);
            if (data is null) { Release(ref reserved); return new LoadedFont(null, null, FontProblem.Unopenable); }
            SKTypeface? face;
            try
            {
                face = TypefaceFactory(data);
            }
            catch
            {
                data.Dispose();
                throw;
            }
            if (face is null)
            {
                data.Dispose();
                Release(ref reserved);
                return new LoadedFont(null, null, FontProblem.Unreadable);
            }
            loaded = true;
            return new LoadedFont(face, data, FontProblem.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Opening or reading failed: perhaps only for now (another program holds the file), so not cached.
            Release(ref reserved);
            return new LoadedFont(null, null, FontProblem.Unopenable);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // The same filter as StoredFont: a parser failure becomes "could not be read", never an exception.
            Release(ref reserved);
            return new LoadedFont(null, null, FontProblem.Unreadable);
        }
        finally
        {
            // Whatever left this method without a loaded font, an exception no catch above names included, gives the
            // reserved bytes back: otherwise the budget would shrink for good and later fonts would be refused.
            if (!loaded) Release(ref reserved);
        }
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes of <paramref name="stream"/> into a new SKData through a small buffer, so
    /// the file exists once in memory (in Skia's block) instead of twice. The copy is done in managed code rather
    /// than by Skia reading the stream, so an IO error is an ordinary .NET exception and never crosses native frames.
    /// Null when the file turned out shorter than it said, or when Skia could not set aside the memory (the caller
    /// reports both as "could not be opened", which is not remembered, so the font is tried again later).
    /// </summary>
    private SKData? ReadInto(Stream stream, long length)
    {
        var data = DataFactory(length);
        if (data is null) return null;
        if (data.Data == IntPtr.Zero)
        {
            data.Dispose();
            return null;
        }
        var buffer = new byte[81920];
        long done = 0;
        try
        {
            while (done < length)
            {
                var n = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length - done));
                if (n == 0) { data.Dispose(); return null; }
                Marshal.Copy(buffer, 0, data.Data + (nint)done, n);
                done += n;
            }
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    /// <summary>Gives back bytes reserved for a load that failed.</summary>
    private void Release(ref long reserved)
    {
        if (reserved > 0) Interlocked.Add(ref _loadedBytes, -reserved);
        reserved = 0;
    }

    /// <summary>A loaded font, the bytes it reads its glyphs from, and why it is missing when it is.</summary>
    /// <param name="Typeface">The font, or null.</param>
    /// <param name="Data">The font file's bytes, held for the life of the library: a typeface reads its glyphs from them lazily.</param>
    /// <param name="Problem">Why <paramref name="Typeface"/> is null.</param>
    private sealed record LoadedFont(SKTypeface? Typeface, SKData? Data, FontProblem Problem);
}

/// <summary>Why a font file named in a label could not be used.</summary>
internal enum FontProblem
{
    /// <summary>No problem, or (when no font came back) the name is not in the FontsFolder at all.</summary>
    None,

    /// <summary>The file is there but is not a TrueType or OpenType font Skia can read.</summary>
    Unreadable,

    /// <summary>The file could not be opened right now (in use by another program, removed, or replaced by a link).</summary>
    Unopenable,

    /// <summary>The font would take LabelScope past <see cref="FontLibrary.MaxLoadedBytes"/> of loaded fonts.</summary>
    OverBudget,
}
