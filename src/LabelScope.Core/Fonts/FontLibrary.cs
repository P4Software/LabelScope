using System.Collections.Concurrent;
using System.Security;
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

    // "ARIAL.TTF" -> full path. Filled once by FromFolder and never changed, so reading it needs no lock. Every key is
    // a bare file name taken from the folder listing; a ZPL name is only ever compared against these keys.
    private readonly Dictionary<string, string> _files;

    // Fonts loaded so far, by full path. The keys can only come from _files, so the cache never holds more than
    // MaxFiles entries and needs no eviction. Lazy makes each file load once even when two renders ask at once
    // (ConcurrentDictionary.GetOrAdd alone may run its factory twice under contention).
    private readonly ConcurrentDictionary<string, Lazy<LoadedFont>> _loaded = new(StringComparer.Ordinal);

    private FontLibrary(Dictionary<string, string> files) => _files = files;

    /// <summary>No folder: only the built-in fonts.</summary>
    public static FontLibrary Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Number of font files found.</summary>
    public int Count => _files.Count;

    /// <summary>
    /// Lists the fonts in <paramref name="folder"/> (relative to <paramref name="programFolder"/> when not rooted).
    /// Problems are added to <paramref name="messages"/> in plain language; the result is never null and this method
    /// never throws for a bad setting or an unreadable folder. The setting is only read, never written back.
    /// </summary>
    public static FontLibrary FromFolder(string? folder, string programFolder, List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(folder)) return Empty;
        ArgumentNullException.ThrowIfNull(messages);

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var tooBig = 0;
        try
        {
            var path = Path.IsPathRooted(folder) ? folder : Path.Combine(programFolder, folder);
            if (!Directory.Exists(path))
            {
                messages.Add($"The FontsFolder in settings.json ({folder}) does not exist, so only LabelScope's built-in fonts are used. Create the folder or correct the setting, then restart LabelScope.");
                return Empty;
            }

            var seen = 0;
            // Top level only: the user named this one folder, and fonts in its subfolders were not offered.
            foreach (var file in new DirectoryInfo(path).EnumerateFiles())
            {
                if (file.Extension.ToUpperInvariant() is not (".TTF" or ".OTF")) continue;
                // Links (symbolic links, junction-like reparse points) could point anywhere on the disk; only real
                // files that sit in the folder itself are offered to labels.
                if ((file.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if (++seen > MaxFiles)
                {
                    messages.Add($"The FontsFolder holds more than {MaxFiles} font files; only the first {MaxFiles} are used. Move the fonts you do not need to another folder, then restart LabelScope.");
                    break;
                }
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
            return new FontLibrary(files);
        }
        if (tooBig > 0)
            messages.Add(tooBig == 1
                ? "1 font file in the FontsFolder is larger than 32 MB and was skipped. No label font is that large: check that it really is a font, then restart LabelScope."
                : $"{tooBig} font files in the FontsFolder are larger than 32 MB and were skipped. No label font is that large: check that they really are fonts, then restart LabelScope.");
        return new FontLibrary(files);
    }

    /// <summary>
    /// The font whose file name is <paramref name="fileName"/> (for example ARIAL.TTF, any case), loaded once and kept.
    /// <paramref name="unreadable"/> is true when the file was listed but is not a font Skia can read (or can no longer
    /// be opened). Anything that is not a plain file name (a drive, a folder, "..") finds nothing.
    /// </summary>
    internal SKTypeface? Find(string fileName, out bool unreadable)
    {
        unreadable = false;
        // Defence in depth: the listing only holds bare names, so a path could never match anyway. Refusing path
        // characters here keeps that true even if the listing ever changes.
        if (fileName.Length == 0 || fileName.IndexOfAny(PathCharacters) >= 0 || fileName.Contains("..", StringComparison.Ordinal))
            return null;
        if (!_files.TryGetValue(fileName.ToUpperInvariant(), out var path)) return null;
        var face = _loaded.GetOrAdd(path, static p => new Lazy<LoadedFont>(() => LoadedFont.Load(p))).Value.Typeface;
        unreadable = face is null;
        return face;
    }

    // Separators and the drive colon for Windows and Unix alike, plus NUL.
    private static readonly char[] PathCharacters = ['\\', '/', ':', '\0'];

    /// <summary>A loaded font and the bytes it reads its glyphs from.</summary>
    private sealed class LoadedFont
    {
        /// <summary>The font, or null when the file could not be read as one.</summary>
        public SKTypeface? Typeface { get; private init; }

        /// <summary>The font file's bytes, held for the life of the library: a typeface reads its glyphs from them lazily.</summary>
        public SKData? Data { get; private init; }

        /// <summary>
        /// Reads the file into memory (so it is not kept open and the user can replace or delete it) and hands it to
        /// Skia. The size is checked again here, because the file may have changed since the folder was listed.
        /// Never throws: any failure is an unreadable font.
        /// </summary>
        public static LoadedFont Load(string path)
        {
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > MaxFontFileBytes || stream.Length == 0) return new LoadedFont();
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }
                var data = SKData.CreateCopy(bytes);
                var face = SKTypeface.FromData(data);
                if (face is null) { data.Dispose(); return new LoadedFont(); }
                return new LoadedFont { Typeface = face, Data = data };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                return new LoadedFont();
            }
        }
    }
}
