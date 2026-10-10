using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelScope.Core.Jobs;

/// <summary>
/// A job as written to jobs.json: the raw facts needed to show it again after a restart (its ZPL is redrawn, and the
/// texts a person reads are produced again in the language of that moment).
/// </summary>
/// <param name="Id">Identifies the job.</param>
/// <param name="Name">The job's own name; empty when the default ("Label") applies.</param>
/// <param name="RemoteAddress">The sender's IP address; null for a file or a paste.</param>
/// <param name="ResolvedHost">The sender's short host name when it was found; otherwise null.</param>
/// <param name="Origin">How the job reached LabelScope.</param>
/// <param name="ReceivedAt">When the job arrived.</param>
/// <param name="Zpl">The ZPL exactly as received.</param>
/// <param name="Complete">False when the sender stopped before the last ^XZ.</param>
public sealed record SavedJob(Guid Id, string Name, string? RemoteAddress, string? ResolvedHost, JobOrigin Origin,
                              DateTimeOffset ReceivedAt, string Zpl, bool Complete);

/// <summary>
/// Keeps the job list between runs ("Keep jobs after closing LabelScope") in jobs.json, as
/// <c>{ "Version": 1, "Jobs": [...] }</c>. Neither method ever throws: a file that cannot be read gives an empty list
/// and a plain-language message, so a bad file never stops LabelScope from starting.
/// </summary>
/// <remarks>
/// Version 1 is the first released layout (0.4.0). A file from a newer LabelScope is never overwritten: after
/// <see cref="Load"/> has seen one, <see cref="Save"/> refuses, so going back to an older version cannot destroy the
/// jobs a newer one kept.
/// </remarks>
public sealed class JobStore
{
    /// <summary>The file format version this LabelScope writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Default largest size of jobs.json (50 MB): bigger files would make every start slow.</summary>
    public const long DefaultMaxBytes = 50_000_000;

    // Temp files older than this are leftovers of a save that was cut off (power loss, crash); younger ones may belong
    // to a save in progress by a second LabelScope window and are left alone.
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromMinutes(10);

    // Readable text for names and ZPL: the default encoder would write every accented letter (and '<', '>', '&', which
    // ZPL uses) as a six-character \u escape, so a Windows-1252 graphic download could grow up to six times.
    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly long _maxBytes;

    // Set by Load when the file was written by a newer LabelScope; Save then refuses to overwrite it.
    private volatile int _newerVersionSeen;

    /// <summary>Creates a store for the file at <paramref name="path"/>.</summary>
    /// <param name="path">Where jobs.json lives; usually <see cref="DefaultPath"/>.</param>
    /// <param name="maxBytes">Largest total size of the saved jobs; older jobs are dropped to stay below it.</param>
    public JobStore(string path, long maxBytes = DefaultMaxBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _maxBytes = maxBytes;
    }

    /// <summary>%LocalAppData%\LabelScope\jobs.json: per user and outside the program folder, which may be read-only.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LabelScope", "jobs.json");

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Writes <paramref name="jobs"/> to the file, keeping only the newest ones: at most <paramref name="limit"/>
    /// jobs, and together at most the size given to the constructor. A single job bigger than that is skipped, and
    /// older jobs that still fit are kept. Kept jobs stay in the order given. The new file is written next to the old
    /// one and then swapped in, so a failure halfway never leaves a broken file; leftover temp files of earlier
    /// failures are removed. Refuses (and changes nothing) when <see cref="Load"/> found a file from a newer version.
    /// </summary>
    /// <returns>Success, or failure with a message saying what to do.</returns>
    public OperationResult Save(IEnumerable<LabelJob> jobs, int limit)
    {
        var newer = _newerVersionSeen;
        if (newer > 0) return OperationResult.Fail(Text.Get("Jobs_SaveRefusedNewer", _path, newer));

        string? temp = null;
        try
        {
            ArgumentNullException.ThrowIfNull(jobs);
            var kept = Keep(jobs.Select(j => j.ToSaved()).ToList(), limit);

            var full = Path.GetFullPath(_path);
            var folder = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(folder);
            DeleteStaleTempFiles(folder, Path.GetFileName(full));
            temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new JobFile { Version = CurrentVersion, Jobs = kept }, Options);

            // Same folder as the target, so Replace and Move are a swap rather than a copy.
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
            temp = null;
            return OperationResult.Ok(Text.Get("Jobs_Saved", kept.Count, full));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or System.Security.SecurityException
                                       or JsonException or InvalidOperationException)
        {
            return OperationResult.Fail(Text.Get("Jobs_SaveFailed", _path, ex.Message));
        }
        finally
        {
            if (temp is not null) TryDelete(temp);
        }
    }

    /// <summary>The newest jobs within the count and size limits, in their original order.</summary>
    private List<SavedJob> Keep(List<SavedJob> saved, int limit)
    {
        // Newest first by arrival time; the stable sort keeps the given order for jobs that arrived at the same moment.
        var newestFirst = saved.Select((job, index) => (job, index))
                               .OrderByDescending(x => x.job.ReceivedAt)
                               .ToList();
        var keep = new HashSet<int>();
        long total = 0;
        foreach (var (job, index) in newestFirst)
        {
            if (keep.Count >= limit) break;
            // The real size in the file, so the limit holds however much escaping the text needs.
            long size = JsonSerializer.SerializeToUtf8Bytes(job, Options).Length;
            // One huge job must not cost the user every older job: skip it and keep filling with the rest.
            if (total + size > _maxBytes) continue;
            total += size;
            keep.Add(index);
        }
        return saved.Where((_, i) => keep.Contains(i)).ToList();
    }

    /// <summary>Removes "jobs.json.*.tmp" files left by a save that was cut off; one that cannot be removed is left.</summary>
    private static void DeleteStaleTempFiles(string folder, string fileName)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, fileName + ".*.tmp"))
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > StaleTempAge) TryDelete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string file)
    {
        // Failing to remove a temp file changes nothing for the user; the next save tries again.
        try { File.Delete(file); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Reads the saved jobs. A missing file gives an empty list and no message. A file that is locked, damaged, or
    /// written by a newer LabelScope gives an empty list and a message; the file itself is left untouched.
    /// </summary>
    public (IReadOnlyList<SavedJob> Jobs, string? Message) Load()
    {
        try
        {
            if (!File.Exists(_path)) return ([], null);
            JobFile? file;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                file = ReadFile(stream);

            if (file is null || file.Version < 1) return ([], Text.Get("Jobs_LoadDamaged", _path, Text.Get("Jobs_NoVersion")));
            if (file.Version > CurrentVersion)
            {
                _newerVersionSeen = file.Version;
                return ([], Text.Get("Jobs_LoadNewer", _path));
            }

            // The parser happily puts null into non-nullable properties of a hand-edited file; repair or drop those.
            var jobs = (file.Jobs ?? [])
                .Where(j => j is not null && !string.IsNullOrEmpty(j.Zpl))
                .Select(j => j with { Name = j.Name ?? "" })
                .ToList();
            return (jobs, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            // Another program (antivirus, a backup tool) can hold the file; the jobs come back at the next start.
            return ([], Text.Get("Jobs_LoadFailed", _path, ex.Message));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return ([], Text.Get("Jobs_LoadDamaged", _path, ex.Message));
        }
    }

    /// <summary>
    /// Reads the version first and the jobs only for a version this LabelScope knows: a newer layout may not even parse
    /// as today's job list, and it must be reported as "newer", not as "damaged".
    /// </summary>
    private static JobFile? ReadFile(Stream stream)
    {
        using var doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        var file = new JobFile();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (string.Equals(p.Name, "Version", StringComparison.OrdinalIgnoreCase))
                file.Version = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var v) ? v : 0;
        if (file.Version != CurrentVersion) return file;
        file.Jobs = doc.RootElement.Deserialize<JobFile>(Options)?.Jobs;
        return file;
    }

    /// <summary>The file layout.</summary>
    private sealed class JobFile
    {
        public int Version { get; set; }
        public List<SavedJob>? Jobs { get; set; }
    }
}
