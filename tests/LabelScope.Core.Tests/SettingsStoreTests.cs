// tests/LabelScope.Core.Tests/SettingsStoreTests.cs
using LabelScope.Core.Settings;
using Xunit;

namespace LabelScope.Core.Tests;

/// <summary>Tests for loading settings.json: creation of the starter file, validation and error messages.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    // Each test gets its own temp folder so tests can run in parallel without touching each other's files.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ls-settings-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test folder.</summary>
    public SettingsStoreTests() => Directory.CreateDirectory(_dir);

    /// <summary>Removes the per-test folder and everything in it.</summary>
    public void Dispose() => Directory.Delete(_dir, true);

    private string P => Path.Combine(_dir, "settings.json");

    [Fact]
    public void MissingFile_IsCreatedWithComments_AndDefaultsAreReturned()
    {
        var result = new SettingsStore().LoadOrCreate(P);

        Assert.True(File.Exists(P));
        Assert.Contains("//", File.ReadAllText(P));
        Assert.Equal(9100, result.Settings.ListenPort);
        Assert.Single(result.Messages); // tells the user a file was created
    }

    [Fact]
    public void StarterFile_LoadsBackWithoutMessages()
    {
        var store = new SettingsStore();
        store.LoadOrCreate(P);

        var second = store.LoadOrCreate(P);

        Assert.Empty(second.Messages);
        Assert.Equal("127.0.0.1", second.Settings.ListenAddress);
    }

    [Fact]
    public void InvalidPort_FallsBackToDefault_AndNamesTheKey()
    {
        File.WriteAllText(P, "{ \"ListenPort\": 70000, \"ListenAddress\": \"127.0.0.1\" }");

        var result = new SettingsStore().LoadOrCreate(P);

        Assert.Equal(9100, result.Settings.ListenPort);
        Assert.Contains(result.Messages, m => m.Contains("ListenPort"));
    }

    [Fact]
    public void BrokenJson_UsesDefaults_AndDoesNotOverwriteTheFile()
    {
        File.WriteAllText(P, "{ this is not json");

        var result = new SettingsStore().LoadOrCreate(P);

        Assert.Equal(9100, result.Settings.ListenPort);
        Assert.Equal("{ this is not json", File.ReadAllText(P));
        Assert.Contains(result.Messages, m => m.Contains("settings file"));
    }

    [Fact]
    public void CommentsAndTrailingCommas_AreAccepted()
    {
        File.WriteAllText(P, "{ // my port\n \"ListenPort\": 9200, }");

        var result = new SettingsStore().LoadOrCreate(P);

        Assert.Equal(9200, result.Settings.ListenPort);
        Assert.Empty(result.Messages);
    }

    [Fact]
    public void UnsupportedDpi_FallsBackTo203()
    {
        File.WriteAllText(P, "{ \"DefaultDpi\": 250 }");

        var result = new SettingsStore().LoadOrCreate(P);

        Assert.Equal(203, result.Settings.DefaultDpi);
        Assert.Contains(result.Messages, m => m.Contains("DefaultDpi"));
    }
}
