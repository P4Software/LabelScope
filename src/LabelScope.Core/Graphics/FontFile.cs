namespace LabelScope.Core.Graphics;

/// <summary>
/// Cheap structural check of a downloaded font file, made when it is stored. The font is parsed later, once, when a
/// label first uses it (see StoredFont), so this check reads a few header bytes and never hands untrusted data to a
/// font parser.
/// </summary>
internal static class FontFile
{
    /// <summary>Largest font file accepted (16 MB, the general ~DY file ceiling).</summary>
    public const long MaxFileBytes = GraphicData.MaxFileBytes;

    /// <summary>
    /// Returns null when <paramref name="file"/> looks like a single TrueType or OpenType font, otherwise a plain
    /// sentence saying why it was refused.
    /// </summary>
    public static string? Check(byte[] file)
    {
        var notAFont = Text.Get("Graphics_NotAFont");
        // sfnt header: 4 byte version, uint16 numTables, 6 more bytes; then 16 bytes per table record.
        if (file.Length < 12) return notAFont;
        var magic = (uint)(file[0] << 24 | file[1] << 16 | file[2] << 8 | file[3]);
        switch (magic)
        {
            case 0x00010000: // TrueType outlines
            case 0x74727565: // 'true' (old Apple TrueType)
            case 0x4F54544F: // 'OTTO' (OpenType with CFF outlines)
                break;
            case 0x74746366: // 'ttcf'
                return Text.Get("Graphics_FontCollection");
            default:
                return notAFont;
        }
        // A real font lists at least one table, and the whole directory must fit in the file.
        var numTables = file[4] << 8 | file[5];
        return numTables < 1 || 12L + 16L * numTables > file.Length ? notAFont : null;
    }
}
