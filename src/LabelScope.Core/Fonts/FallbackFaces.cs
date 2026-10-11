using System.Collections.Concurrent;
using SkiaSharp;

namespace LabelScope.Core.Fonts;

/// <summary>
/// Installed Windows fonts that stand in for characters the bundled fonts lack (Japanese, Chinese, Greek letters in a
/// cell font...). A printer with the right font loaded prints them; drawing them from a Windows font shows the user the
/// text that arrived instead of a row of spaces. Asked only for characters the field's own font has no glyph for.
/// </summary>
internal static class FallbackFaces
{
    /// <summary>
    /// Most new characters one field may look up. A lookup costs about 15 microseconds, so a hostile field of 65536
    /// different characters is held to a few milliseconds; the rest are treated as missing, as without a fallback.
    /// </summary>
    public const int MaxLookupsPerText = 1024;

    // Most characters remembered for the whole process; past it, answers are looked up again rather than kept, so a
    // stream of hostile text cannot grow the cache without end.
    private const int MaxCached = 65_536;

    // Answers by code point, shared by every render (null = no installed font has it). The typefaces come from Skia's
    // font manager, which hands back one shared instance per face, and they are only ever read: never disposed here.
    private static readonly ConcurrentDictionary<int, SKTypeface?> Cache = new();

    /// <summary>True when <paramref name="codePoint"/> was already looked up, so asking again costs nothing.</summary>
    public static bool IsKnown(int codePoint) => Cache.ContainsKey(codePoint);

    /// <summary>
    /// An installed typeface that has a glyph for <paramref name="codePoint"/>, or null when none has. The answer is
    /// checked against the face itself, since the font manager may return its best guess for a character it lacks.
    /// </summary>
    public static SKTypeface? For(int codePoint)
    {
        if (Cache.TryGetValue(codePoint, out var known)) return known;
        var face = SKFontManager.Default.MatchCharacter(codePoint);
        if (face is not null && face.GetGlyphs(char.ConvertFromUtf32(codePoint))[0] == 0) face = null;
        if (Cache.Count < MaxCached) Cache.TryAdd(codePoint, face);
        return face;
    }
}
