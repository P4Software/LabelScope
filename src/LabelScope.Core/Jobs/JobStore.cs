using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabelScope.Core.Jobs;

/// <summary>A job as written to jobs.json: what is needed to show it again after a restart (its ZPL is redrawn).</summary>
/// <param name="Id">Identifies the job.</param>
/// <param name="Name">Name shown on the card.</param>
/// <param name="Source">Source shown on the card.</param>
/// <param name="Origin">How the job reached LabelScope.</param>
/// <param name="ReceivedAt">When the job arrived.</param>
/// <param name="Zpl">The ZPL exactly as received.</param>
/// <param name="Complete">False when the sender stopped before the last ^XZ.</param>
public sealed record SavedJob(Guid Id, string Name, string Source, JobOrigin Origin, DateTimeOffset ReceivedAt,
                              string Zpl, bool Complete);

/// <summary>
/// Keeps the job list between runs ("Keep jobs after closing LabelScope") in jobs.json, as
/// <c>{ "Version": 1, "Jobs": [...] }</c>. Neither method ever throws: a file that cannot be read gives an empty list
/// and a plain-language message, so a bad file never stops LabelScope from starting.
/// </summary>
public sealed class JobStore
{
    /// <summary>The file format version this LabelScope writes and reads.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Default largest size of jobs.json (50 MB): bigger files would make every start slow.</summary>
    public const long DefaultMaxBytes = 50_000_000;

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
    /// jobs, and together at most the size given to the constructor (a single job bigger than that is not saved). Kept
    /// jobs stay in the order given. The new file is written next to the old one and then swapped in, so a failure
    /// halfway never leaves a broken file.
    /// </summary>
    /// <returns>Success, or failure with a message saying what to do.</returns>
    public OperationResult Save(IEnumerable<LabelJob> jobs, int limit)
    {
        string? temp = null;
        try
        {
            ArgumentNullException.ThrowIfNull(jobs);
            var saved = jobs.Select(j => j.ToSaved()).ToList();
            var kept = Keep(saved, limit);

            var full = Path.GetFullPath(_path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
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
                                       or JsonException)
        {
            return OperationResult.Fail(Text.Get("Jobs_SaveFailed", _path, ex.Message));
        }
        finally
        {
            if (temp is not null)
            {
                try { File.Delete(temp); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
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
            if (total + size > _maxBytes) break;
            total += size;
            keep.Add(index);
        }
        return saved.Where((_, i) => keep.Contains(i)).ToList();
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
                file = JsonSerializer.Deserialize<JobFile>(stream, Options);

            if (file is null || file.Version < 1) return ([], Text.Get("Jobs_LoadDamaged", _path, Text.Get("Jobs_NoVersion")));
            if (file.Version > CurrentVersion) return ([], Text.Get("Jobs_LoadNewer", _path));

            // The parser happily puts null into non-nullable properties of a hand-edited file; repair or drop those.
            var jobs = (file.Jobs ?? [])
                .Where(j => j is not null && !string.IsNullOrEmpty(j.Zpl))
                .Select(j => j with { Name = j.Name ?? "", Source = j.Source ?? "" })
                .ToList();
            return (jobs, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
                                       or NotSupportedException or ArgumentException)
        {
            // Another program (antivirus, a backup tool) can hold the file; the jobs come back at the next start.
            return ([], Text.Get("Jobs_LoadFailed", _path, ex.Message));
        }
        catch (JsonException ex)
        {
            return ([], Text.Get("Jobs_LoadDamaged", _path, ex.Message));
        }
    }

    /// <summary>The file layout.</summary>
    private sealed class JobFile
    {
        public int Version { get; set; }
        public List<SavedJob>? Jobs { get; set; }
    }
}
