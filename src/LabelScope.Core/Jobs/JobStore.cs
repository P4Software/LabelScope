using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LabelScope.Core.Samples;

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
/// <param name="ReadAsWindows1252">True when the job's bytes were not valid UTF-8 and were read as Windows-1252;
/// absent (false) in files written before it was kept, which only loses a ^CI28 warning.</param>
/// <param name="Epoch">The printer epoch the job was drawn in (see <see cref="LabelJob.Epoch"/>); absent (null) in files
/// written before 0.5.0, whose jobs then all count as one earlier epoch. Adding it needs no new file version: older
/// LabelScope versions ignore a member they do not know, so they can still read and write the file.</param>
/// <param name="SampleId">The id (slug) of the built-in sample label this job shows; null for every other job. A sample job
/// is written with <c>Origin</c> <see cref="JobOrigin.OpenedFromFile"/> plus this member (see <see cref="LabelJob.ToSaved"/>),
/// in the <c>Samples</c> array of the file, not in <c>Jobs</c> (see <see cref="JobStore"/>). Adding it needs no new file
/// version, for the same reason as <paramref name="Epoch"/>: 0.5.1 ignores the members it does not know, so it can still
/// read and write the file. Its next save leaves the <c>Samples</c> array out; the sample cards are built in and can be
/// opened again. Not written when null, so a file without samples keeps the exact shape 0.5.1 writes. Absent in files
/// written before 0.5.2.</param>
public sealed record SavedJob(Guid Id, string Name, string? RemoteAddress, string? ResolvedHost, JobOrigin Origin,
                              DateTimeOffset ReceivedAt, string Zpl, bool Complete, bool ReadAsWindows1252 = false,
                              Guid? Epoch = null,
                              [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SampleId = null);

/// <summary>
/// Keeps the job list between runs ("Keep jobs after closing LabelScope") in jobs.json, as
/// <c>{ "Version": 1, "Jobs": [...], "Samples": [...] }</c> (<c>Samples</c> only when sample labels are kept). Neither method ever throws: a file that cannot be read gives an empty list
/// and a plain-language message, so a bad file never stops LabelScope from starting.
/// </summary>
/// <remarks>
/// Version 1 is the first released layout (0.4.0). A file from a newer LabelScope is never overwritten: after
/// <see cref="Load"/> has seen one, <see cref="Save"/> refuses, so going back to an older version cannot destroy the
/// jobs a newer one kept. In the same way, after <see cref="Load"/> could not open the file at all (another program
/// held it, or access was denied), <see cref="Save"/> refuses until a later <see cref="Load"/> succeeds: the jobs in
/// that file were never read, so writing this session's list would erase them. A damaged file is different: its jobs
/// are already lost, so it may be replaced.
/// <para>
/// Sample labels (see <see cref="JobOrigin.Sample"/>) are kept in their own <c>Samples</c> array and not in <c>Jobs</c>.
/// 0.5.1 counts every entry of <c>Jobs</c> toward its HistoryLimit when it restores and saves, so sample entries there
/// would push out up to 25 real jobs for anyone who goes back to it. It skips a root member it does not know, so with a
/// separate array it keeps exactly the real jobs, and its next save drops only the samples (built in, can be opened
/// again). <see cref="Load"/> merges both arrays into the one list it returns, and no file version change is needed.
/// </para>
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

    // Set by Load when the file exists but could not be opened (locked, access denied); cleared by a Load that gets
    // past opening the file. While set, Save refuses: the file's jobs were never read, so they are not in the list.
    private volatile bool _loadFailed;

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
    /// True after the latest <see cref="Load"/> could not open an existing file (locked by another program, access
    /// denied). <see cref="Save"/> refuses while this is true, so jobs that were never read are not overwritten.
    /// </summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>
    /// Writes <paramref name="jobs"/> to the file, keeping only the newest ones: at most <paramref name="limit"/>
    /// jobs, and together at most the size given to the constructor. Sample labels do not count toward the limit (they
    /// must never push out a real job) but do count toward the size. A single job bigger than that is skipped, and
    /// older jobs that still fit are kept. Kept jobs stay in the order given. The new file is written next to the old
    /// one and then swapped in, so a failure halfway never leaves a broken file; leftover temp files of earlier
    /// failures are removed. Refuses (and changes nothing) when <see cref="Load"/> found a file from a newer version,
    /// or when the latest <see cref="Load"/> could not open the file (see <see cref="LoadFailed"/>).
    /// </summary>
    /// <returns>Success, or failure with a message saying what to do.</returns>
    public OperationResult Save(IEnumerable<LabelJob> jobs, int limit)
    {
        var newer = _newerVersionSeen;
        if (newer > 0) return OperationResult.Fail(Text.Get("Jobs_SaveRefusedNewer", _path, newer));
        if (_loadFailed) return OperationResult.Fail(Text.Get("Jobs_SaveRefusedLoadFailed", _path));

        string? temp = null;
        try
        {
            ArgumentNullException.ThrowIfNull(jobs);
            var all = jobs.Select(j => j.ToSaved()).ToList();
            // Real jobs first, so they have first call on the size budget; the samples get what is left.
            var keptJobs = Keep(all.Where(j => !IsSample(j)).ToList(), limit, _maxBytes, out var used);
            var keptSamples = Keep(all.Where(IsSample).ToList(), int.MaxValue, _maxBytes - used, out _);
            var count = keptJobs.Count + keptSamples.Count;

            var full = Path.GetFullPath(_path);
            var folder = Path.GetDirectoryName(full)!;
            Directory.CreateDirectory(folder);
            DeleteStaleTempFiles(folder, Path.GetFileName(full));
            temp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(stream, new JobFile { Version = CurrentVersion, Jobs = keptJobs, Samples = keptSamples.Count > 0 ? keptSamples : null }, Options);

            // Same folder as the target, so Replace and Move are a swap rather than a copy.
            if (File.Exists(full)) File.Replace(temp, full, null);
            else File.Move(temp, full);
            temp = null;
            return OperationResult.Ok(Text.Get("Jobs_Saved", count, full));
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

    /// <summary>
    /// The newest of <paramref name="saved"/> within <paramref name="limit"/> jobs and <paramref name="budget"/> bytes, in
    /// their original order. Called once for the real jobs (with HistoryLimit) and once for the samples (no count limit).
    /// </summary>
    /// <param name="saved">The jobs to choose from.</param>
    /// <param name="limit">The most jobs to keep.</param>
    /// <param name="budget">The most bytes the kept jobs may take in the file.</param>
    /// <param name="used">The bytes the kept jobs take.</param>
    private static List<SavedJob> Keep(List<SavedJob> saved, int limit, long budget, out long used)
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
            if (total + size > budget) continue;
            total += size;
            keep.Add(index);
        }
        used = total;
        return saved.Where((_, i) => keep.Contains(i)).ToList();
    }

    /// <summary>
    /// The jobs to bring back at start: the newest <paramref name="limit"/> real jobs plus the newest job of each sample,
    /// oldest first (the order they are drawn in). Samples do not use up the limit, so a small HistoryLimit never hides
    /// the real jobs behind sample cards. The window keeps one card per sample, so a file only holds a sample twice when
    /// it was edited by hand; the older copy is left out.
    /// </summary>
    /// <param name="saved">What <see cref="Load"/> returned.</param>
    /// <param name="limit">HistoryLimit: how many real jobs to bring back.</param>
    /// <returns>The jobs to draw, oldest first. The file lists jobs newest first, so jobs that arrived at the same moment
    /// come out in the reverse of their order in <paramref name="saved"/>, and it is the later ones of them that the
    /// limit cuts.</returns>
    public static IReadOnlyList<SavedJob> NewestWithinLimit(IReadOnlyList<SavedJob> saved, int limit)
    {
        ArgumentNullException.ThrowIfNull(saved);
        // Newest first by arrival time, stable for jobs that arrived at the same moment (as Keep does).
        var newestFirst = saved.OrderByDescending(j => j.ReceivedAt);
        var result = new List<SavedJob>();
        var samplesSeen = new HashSet<string>(StringComparer.Ordinal);
        var realKept = 0;
        foreach (var job in newestFirst)
        {
            if (IsSample(job))
            {
                // Newest first, so the first copy of a sample is the one to keep.
                if (samplesSeen.Add(job.SampleId!)) result.Add(job);
            }
            else if (realKept < limit)
            {
                result.Add(job);
                realKept++;
            }
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// True for a job that comes back as a sample label. An id this version does not know (a sample a later version
    /// removed) comes back as an ordinary opened file (see <see cref="LabelJob.FromSaved"/>), so it counts as a real job.
    /// </summary>
    private static bool IsSample(SavedJob job) => SampleLabels.IsKnown(job.SampleId);

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
    /// written by a newer LabelScope gives an empty list and a message; the file itself is left untouched. A file that
    /// could not be opened also makes <see cref="Save"/> refuse until a later call of this method succeeds.
    /// </summary>
    public (IReadOnlyList<SavedJob> Jobs, string? Message) Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _loadFailed = false;
                return ([], null);
            }
            JobFile? file;
            using (var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read))
                file = ReadFile(stream);
            // The file was opened and read to the end; whatever it holds, a save no longer risks unread jobs.
            _loadFailed = false;

            if (file is null || file.Version < 1) return ([], Text.Get("Jobs_LoadDamaged", _path, Text.Get("Jobs_NoVersion")));
            if (file.Version > CurrentVersion)
            {
                _newerVersionSeen = file.Version;
                return ([], Text.Get("Jobs_LoadNewer", _path));
            }

            // The parser happily puts null into non-nullable properties of a hand-edited file; repair or drop those.
            var jobs = (file.Jobs ?? []).Concat(file.Samples ?? [])
                .Where(j => j is not null && !string.IsNullOrEmpty(j.Zpl))
                .Select(j => j with { Name = j.Name ?? "" })
                .ToList();
            return (jobs, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            // Another program (antivirus, a backup tool) can hold the file. Its jobs were not read, so saving now would
            // erase them: Save refuses until a later Load succeeds, and the jobs come back at the next start.
            _loadFailed = true;
            return ([], Text.Get("Jobs_LoadFailed", _path, ex.Message));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Opened but damaged: its jobs are lost anyway, so new jobs may replace the file.
            _loadFailed = false;
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
        // Jobs are read strictly: a job list that does not parse is a damaged file, as it always was.
        file.Jobs = doc.RootElement.Deserialize<JobsOnly>(Options)?.Jobs;
        file.Samples = ReadSamples(doc.RootElement);
        return file;
    }

    /// <summary>
    /// Reads the <c>Samples</c> member without ever failing. It is the downgrade valve (see <see cref="SavedJob"/>), and a
    /// later version may give it another shape; samples are built in and can be opened again, so an unreadable member, or
    /// an unreadable entry in it, is dropped and must never make the file "damaged": the next save would replace the
    /// file and every real job in <c>Jobs</c> would be lost with it.
    /// </summary>
    private static List<SavedJob>? ReadSamples(JsonElement root)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (!string.Equals(p.Name, "Samples", StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Value.ValueKind != JsonValueKind.Array) return null;
            var samples = new List<SavedJob>();
            foreach (var entry in p.Value.EnumerateArray())
            {
                try
                {
                    if (entry.Deserialize<SavedJob>(Options) is { } sample) samples.Add(sample);
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or NotSupportedException)
                {
                    // One entry of another shape: keep the others.
                }
            }
            return samples;
        }
        return null;
    }

    /// <summary>The part of the file that is read strictly. Members it does not list (<c>Samples</c>) are skipped.</summary>
    private sealed class JobsOnly
    {
        public List<SavedJob>? Jobs { get; set; }
    }

    /// <summary>The file layout.</summary>
    private sealed class JobFile
    {
        public int Version { get; set; }
        public List<SavedJob>? Jobs { get; set; }

        // Sample labels, apart from Jobs so that 0.5.1 does not count them toward its limit. Left out when empty.
        // Forward compatibility: this is written strictly but read tolerantly (see ReadSamples). A later version may change
        // the shape of this member, so a member or an entry that does not parse as a SavedJob is dropped on load, and the
        // file is never reported damaged because of it (that would cost the real jobs at the next save).
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<SavedJob>? Samples { get; set; }
    }
}
