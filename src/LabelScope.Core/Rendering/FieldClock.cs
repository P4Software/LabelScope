using System.Globalization;
using System.Text;

namespace LabelScope.Core.Rendering;

/// <summary>
/// The clock indicators a ^FCa,b,c sets for one field: <paramref name="Primary"/> reads the printer clock,
/// <paramref name="Secondary"/> and <paramref name="Third"/> read it moved by ^SO2 and ^SO3.
/// </summary>
/// <param name="Primary">Indicator of the printer clock (default %).</param>
/// <param name="Secondary">Indicator of the ^SO2 clock; null when the ^FC gives none.</param>
/// <param name="Third">Indicator of the ^SO3 clock; null when the ^FC gives none.</param>
internal readonly record struct ClockIndicators(char Primary, char? Secondary, char? Third);

/// <summary>
/// One ^SO offset, in the order the command gives them. Months and years are calendar steps (31 January plus one
/// month is the end of February), so they cannot be a <see cref="TimeSpan"/>.
/// </summary>
internal sealed record ClockShift(int Months, int Days, int Years, int Hours, int Minutes, int Seconds);

/// <summary>
/// What a label sets for its clock fields: the ^SL language of month and day names and the ^SO2 / ^SO3 offsets.
/// </summary>
/// <param name="Language">^SL language number, 1 (English) to 18.</param>
/// <param name="Secondary">The ^SO2 offset; null when the label sets none, which leaves the secondary clock unset.</param>
/// <param name="Third">The ^SO3 offset; null when the label sets none.</param>
internal sealed record ClockSettings(int Language, ClockShift? Secondary, ClockShift? Third);

/// <summary>Why some clock text of a field was left as written; the painter turns each into one warning.</summary>
[Flags]
internal enum ClockProblems
{
    /// <summary>Every placeholder was filled in.</summary>
    None = 0,
    /// <summary>The field used the secondary indicator, but the label sets no ^SO2.</summary>
    NoSecondary = 1,
    /// <summary>The field used the third indicator, but the label sets no ^SO3.</summary>
    NoThird = 2,
    /// <summary>The ^SO2 offset moves the date past what a calendar can show (before year 1 or after 9999).</summary>
    SecondaryOutOfRange = 4,
    /// <summary>The ^SO3 offset moves the date past what a calendar can show.</summary>
    ThirdOutOfRange = 8,
}

/// <summary>
/// The real-time clock of a printer: fills in the placeholders of a ^FC field (zpl-ref-8 section 5) and reads ^FC,
/// ^SO, ^SL and ^ST. All work is done on the wall-clock time of the <see cref="DateTimeOffset"/> it is given, never on
/// the machine's time zone, so a job drawn again later, or on another PC, shows the same text.
/// </summary>
internal static class FieldClock
{
    /// <summary>Largest offset ^SO accepts for each value (the guide's range).</summary>
    private const int MaxShift = 32000;

    /// <summary>Most characters of a bad value quoted back in a warning.</summary>
    private const int MaxShown = 20;

    /// <summary>
    /// The culture whose month and day names stand for each ^SL language (index = language - 1). The guide gives no
    /// name tables for any language but English, so LabelScope takes the names .NET has for the matching culture.
    /// English uses the invariant culture, whose names are the English ones on every machine. "Spanish 2" (10) is not
    /// explained by the guide; Latin-American Spanish is used for it.
    /// </summary>
    private static readonly string[] Cultures =
    [
        "", "es-ES", "fr-FR", "de-DE", "it-IT", "nb-NO", "pt-PT", "sv-SE", "da-DK",
        "es-MX", "nl-NL", "fi-FI", "ja-JP", "ko-KR", "zh-CN", "zh-TW", "ru-RU", "pl-PL",
    ];

    /// <summary>Number of ^SL languages.</summary>
    public static int LanguageCount => Cultures.Length;

    /// <summary>The brief's form, for callers that need no report of what was left as written.</summary>
    public static string Expand(string data, ClockIndicators ind, DateTimeOffset now, ClockSettings s) =>
        Expand(data, ind, now, s, out _);

    /// <summary>
    /// Replaces every indicator-plus-letter of <paramref name="data"/> with the clock value it names. A letter that is
    /// not in the guide's table, an indicator at the very end, and an indicator whose clock is not set stay as written,
    /// which is what a printer prints for text it does not recognise.
    /// </summary>
    /// <param name="data">The field's final data.</param>
    /// <param name="ind">The field's ^FC indicators.</param>
    /// <param name="now">The printer clock; only its wall-clock date and time are used.</param>
    /// <param name="s">The label's ^SL language and ^SO offsets.</param>
    /// <param name="problems">Why some clock text was left as written.</param>
    public static string Expand(string data, ClockIndicators ind, DateTimeOffset now, ClockSettings s, out ClockProblems problems)
    {
        problems = ClockProblems.None;
        if (string.IsNullOrEmpty(data)) return data ?? "";
        // Almost every field has no indicator at all: no copy is made for those.
        if (data.IndexOf(ind.Primary) < 0 && (ind.Secondary is not { } s2 || data.IndexOf(s2) < 0) &&
            (ind.Third is not { } s3 || data.IndexOf(s3) < 0))
            return data;

        var names = Names(s.Language);
        var primary = now.DateTime;
        // The moved clocks are worked out once, and only when a field uses them.
        (bool Done, DateTime? Time) secondary = default, third = default;

        // One pass, character by character: the data is never used as a .NET format string, so nothing in it can
        // become a format instruction, and a field full of indicators costs one step per character.
        var sb = new StringBuilder(data.Length + 16);
        for (var i = 0; i < data.Length; i++)
        {
            var c = data[i];
            if (i + 1 >= data.Length || !IsLetter(data[i + 1]))
            {
                sb.Append(c);
                continue;
            }

            DateTime? time;
            if (c == ind.Primary)
            {
                time = primary;
            }
            else if (c == ind.Secondary)
            {
                if (!secondary.Done) secondary = (true, s.Secondary is { } shift ? Shift(primary, shift) : null);
                time = secondary.Time;
                if (time is null) problems |= s.Secondary is null ? ClockProblems.NoSecondary : ClockProblems.SecondaryOutOfRange;
            }
            else if (c == ind.Third)
            {
                if (!third.Done) third = (true, s.Third is { } shift ? Shift(primary, shift) : null);
                time = third.Time;
                if (time is null) problems |= s.Third is null ? ClockProblems.NoThird : ClockProblems.ThirdOutOfRange;
            }
            else
            {
                sb.Append(c);
                continue;
            }

            if (time is { } t)
            {
                Append(sb, data[i + 1], t, names);
                i++;
            }
            else
            {
                // The clock is not set: indicator and letter stay as written.
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>True for the letters of the guide's table (Table 39); any other character after an indicator is text.</summary>
    private static bool IsLetter(char c) => c is 'a' or 'A' or 'b' or 'B' or 'd' or 'H' or 'I' or 'j' or 'm' or 'M' or
                                                 'p' or 'S' or 'U' or 'W' or 'w' or 'y' or 'Y';

    /// <summary>Appends the value of placeholder <paramref name="letter"/> at <paramref name="t"/>.</summary>
    private static void Append(StringBuilder sb, char letter, DateTime t, DateTimeFormatInfo names)
    {
        var dayOfYear = t.DayOfYear - 1;      // 0-based, as C's tm_yday
        var weekday = (int)t.DayOfWeek;        // 0 = Sunday, as C's tm_wday
        switch (letter)
        {
            case 'a': sb.Append(names.AbbreviatedDayNames[weekday]); break;
            case 'A': sb.Append(names.DayNames[weekday]); break;
            case 'b': sb.Append(names.AbbreviatedMonthNames[t.Month - 1]); break;
            // Nominative names ("март", "marzec"), not the genitive forms some languages use inside a full date: a
            // %B often stands alone on a label.
            case 'B': sb.Append(names.MonthNames[t.Month - 1]); break;
            case 'd': Digits(sb, t.Day, 2); break;
            case 'H': Digits(sb, t.Hour, 2); break;
            // Zero padded: the guide's figure prints 02:30:00 PM.
            case 'I': Digits(sb, t.Hour % 12 == 0 ? 12 : t.Hour % 12, 2); break;
            case 'j': Digits(sb, dayOfYear + 1, 3); break;
            case 'm': Digits(sb, t.Month, 2); break;
            case 'M': Digits(sb, t.Minute, 2); break;
            // Upper-case AM / PM in every language: the guide shows only these, and ^SL is said to change month and
            // day names, not the designators.
            case 'p': sb.Append(t.Hour < 12 ? "AM" : "PM"); break;
            case 'S': Digits(sb, t.Second, 2); break;
            // Weeks as C strftime counts them, not ISO 8601: week 1 is the first full week starting on Sunday (%U) or
            // Monday (%W); the days of January before it are week 0. The guide's January 2005 and 2006 examples agree.
            case 'U': Digits(sb, (dayOfYear + 7 - weekday) / 7, 2); break;
            case 'W': Digits(sb, (dayOfYear + 7 - (weekday + 6) % 7) / 7, 2); break;
            // Two digits (00 = Sunday), as the guide writes it; C would give one.
            case 'w': Digits(sb, weekday, 2); break;
            case 'y': Digits(sb, t.Year % 100, 2); break;
            case 'Y': Digits(sb, t.Year, 4); break;
        }
    }

    private static void Digits(StringBuilder sb, int value, int width) =>
        sb.Append(value.ToString("D" + width.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));

    /// <summary>
    /// The month and day names of ^SL language <paramref name="language"/>. A culture the machine does not have (for
    /// example when .NET runs without its culture data) falls back to English, so names never fail a label.
    /// </summary>
    private static DateTimeFormatInfo Names(int language)
    {
        var name = language >= 1 && language <= Cultures.Length ? Cultures[language - 1] : "";
        if (name.Length == 0) return CultureInfo.InvariantCulture.DateTimeFormat;
        try
        {
            return CultureInfo.GetCultureInfo(name).DateTimeFormat;
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.InvariantCulture.DateTimeFormat;
        }
    }

    /// <summary>
    /// <paramref name="t"/> moved by <paramref name="shift"/>: first the calendar months and years together
    /// (AddMonths, so the 31st becomes the last day of a shorter month), then days, hours, minutes and seconds.
    /// Null when the result is before year 1 or after year 9999, which is checked before any Add so nothing throws.
    /// </summary>
    internal static DateTime? Shift(DateTime t, ClockShift shift)
    {
        var months = shift.Months + 12L * shift.Years;
        var target = t.Year * 12L + (t.Month - 1) + months;
        if (target < 12 || target > 9999L * 12 + 11) return null;
        var moved = t.AddMonths((int)months);
        var ticks = shift.Days * TimeSpan.TicksPerDay + shift.Hours * TimeSpan.TicksPerHour +
                    shift.Minutes * TimeSpan.TicksPerMinute + shift.Seconds * TimeSpan.TicksPerSecond;
        var result = moved.Ticks + ticks;
        return result < DateTime.MinValue.Ticks || result > DateTime.MaxValue.Ticks ? null : new DateTime(result);
    }

    // ---- reading the commands ---------------------------------------------------------------------

    /// <summary>
    /// ^FCa,b,c: the field's indicators. An empty or missing primary is the default %; secondary and third have no
    /// default and stay unset unless given.
    /// </summary>
    public static ClockIndicators ReadIndicators(string args)
    {
        var a = args.Split(',');
        char? At(int i) => i < a.Length && a[i].Length > 0 ? a[i][0] : null;
        return new ClockIndicators(At(0) ?? '%', At(1), At(2));
    }

    /// <summary>
    /// ^SOa,b,c,d,e,f,g: which clock (2 or 3) and its offset. Values are kept to the guide's ±32000; a value that is
    /// not a number counts as 0. Returns false, with the clock text quoted for the warning, when a is neither 2 nor 3.
    /// </summary>
    public static bool TryReadShift(string args, out int clock, out ClockShift shift, out string shown)
    {
        var a = args.Split(',');
        var which = a[0].Trim();
        shown = which.Length > MaxShown ? which[..MaxShown] + "..." : which;
        clock = which == "2" ? 2 : which == "3" ? 3 : 0;
        int V(int i) => i < a.Length && long.TryParse(a[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? (int)Math.Clamp(v, -MaxShift, MaxShift)
            : 0;
        shift = new ClockShift(V(1), V(2), V(3), V(4), V(5), V(6));
        return clock != 0;
    }

    /// <summary>
    /// ^SLa,b: the language of names. Null when b is not given (the language stays as it was); otherwise the number,
    /// or 0 with <paramref name="shown"/> filled when b is not a language 1-18.
    /// </summary>
    public static int? ReadLanguage(string args, out string shown)
    {
        var a = args.Split(',');
        var text = a.Length > 1 ? a[1].Trim() : "";
        shown = text.Length > MaxShown ? text[..MaxShown] + "..." : text;
        if (text.Length == 0) return null;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v) && v >= 1 && v <= Cultures.Length ? v : 0;
    }

    /// <summary>
    /// ^STa,b,c,d,e,f,g: the date and time to set (month, day, year, hour, minute, second, format). A value left out
    /// keeps the printer clock's <paramref name="current"/> value. With format A or P the hour is a 12-hour value: the
    /// guide's own sample sets 02,…,P and then prints 14:30, so P adds 12 (12 A is midnight, 12 P is noon); an hour
    /// above 12 is already a 24-hour value. False, and nothing set, for a date or time that does not exist.
    /// </summary>
    public static bool TryReadSetTime(string args, DateTime current, out DateTime set)
    {
        set = current;
        var a = args.Split(',');
        var ok = true;
        int Part(int i, int fallback)
        {
            var text = i < a.Length ? a[i].Trim() : "";
            if (text.Length == 0) return fallback;
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return v;
            ok = false;
            return 0;
        }

        var month = Part(0, current.Month);
        var day = Part(1, current.Day);
        var year = Part(2, current.Year);
        var hourText = a.Length > 3 ? a[3].Trim() : "";
        var hour = Part(3, current.Hour);
        var minute = Part(4, current.Minute);
        var second = Part(5, current.Second);
        var format = ZplArgs.Letter(a, 6, 'M');
        if (!ok || format is not ('M' or 'A' or 'P')) return false;
        // A given hour of 0-12 with A or P is a 12-hour value; a left-out hour is the clock's own 24-hour value.
        if (format != 'M' && hourText.Length > 0 && hour <= 12) hour = hour % 12 + (format == 'P' ? 12 : 0);
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) ||
            hour > 23 || minute > 59 || second > 59)
            return false;
        set = new DateTime(year, month, day, hour, minute, second);
        return true;
    }

    // ---- the printer clock -------------------------------------------------------------------------

    /// <summary>
    /// The printer clock at <paramref name="now"/>: the job's own time, or, after a ^ST, the set time moved on by the
    /// real time since. <paramref name="offset"/> is kept against UTC, so a later job in another UTC offset (summer
    /// time began) still adds the true time that passed, as a battery clock does.
    /// </summary>
    public static DateTimeOffset PrinterNow(DateTimeOffset now, TimeSpan? offset)
    {
        if (offset is not { } o) return now;
        var ticks = now.UtcDateTime.Ticks + o.Ticks;
        // Only reachable with a test clock near the ends of the calendar; the PC clock is used instead.
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks) return now;
        return new DateTimeOffset(new DateTime(ticks), TimeSpan.Zero);
    }

    /// <summary>The offset to keep in printer setup so the clock reads <paramref name="set"/> at <paramref name="now"/>.</summary>
    public static TimeSpan OffsetFor(DateTime set, DateTimeOffset now) => set - now.UtcDateTime;
}
