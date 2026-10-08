using LabelScope.Core;
using Xunit;

namespace LabelScope.Core.Tests;

public sealed class HistoryBudgetTests
{
    [Fact]
    public void SizeOf_CountsPngBytesPlusTwoBytesPerZplCharacter()
    {
        Assert.Equal(1000 + 2 * 10, HistoryBudget.SizeOf(1000, "0123456789"));
        Assert.Equal(500, HistoryBudget.SizeOf(500, null));
    }

    [Fact]
    public void CountLimit_StillApplies()
    {
        var sizes = Enumerable.Repeat(1L, 10).ToList();
        Assert.Equal(3, HistoryBudget.EntriesToKeep(sizes, 3, 1000));
    }

    [Fact]
    public void ByteLimit_TrimsTheOldest()
    {
        // Newest first: 100 + 100 + 100 fits in 300, the fourth would not.
        var sizes = new List<long> { 100, 100, 100, 100, 100 };
        Assert.Equal(3, HistoryBudget.EntriesToKeep(sizes, 100, 300));
    }

    [Fact]
    public void NewestEntry_IsKept_EvenWhenItAloneIsOverTheBudget()
    {
        var sizes = new List<long> { 5000, 10 };
        Assert.Equal(1, HistoryBudget.EntriesToKeep(sizes, 100, 300));
    }

    [Fact]
    public void EmptyHistory_KeepsNothing()
    {
        Assert.Equal(0, HistoryBudget.EntriesToKeep(new List<long>(), 100));
    }

    [Fact]
    public void DefaultBudget_Is256Megabytes()
    {
        Assert.Equal(256L * 1024 * 1024, HistoryBudget.DefaultMaxBytes);
    }
}
