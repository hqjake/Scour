using Scour.Core.Processes;

namespace Scour.Tests;

public class ProcessTreeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1);

    private static List<ProcessNode> Sample() =>
    [
        new(4, 0, T0, "System"),
        new(100, 4, T0.AddSeconds(1), "smss"),
        new(500, 1, T0.AddSeconds(5), "explorer"),          // parent 1 doesn't exist → root
        new(600, 500, T0.AddSeconds(10), "chrome"),
        new(601, 600, T0.AddSeconds(11), "chrome"),
        new(602, 600, T0.AddSeconds(12), "chrome"),
        new(700, 500, T0.AddSeconds(20), "code"),
        new(800, 900, T0.AddSeconds(30), "orphan"),         // parent 900 started later → PID reuse → root
        new(900, 4, T0.AddSeconds(40), "late"),
    ];

    [Fact]
    public void Builds_depth_first_tree_sorted_by_name()
    {
        var rows = ProcessTree.Flatten(Sample());
        Assert.Equal([500, 600, 601, 602, 700, 800, 4, 900, 100], rows.Select(r => r.Pid));
        Assert.Equal([0, 1, 2, 2, 1, 0, 0, 1, 1], rows.Select(r => r.Depth));
        Assert.True(rows.Single(r => r.Pid == 600).HasChildren);
        Assert.False(rows.Single(r => r.Pid == 601).HasChildren);
    }

    [Fact]
    public void Collapsed_nodes_hide_descendants()
    {
        var rows = ProcessTree.Flatten(Sample(), new HashSet<int> { 500 });
        Assert.Equal([500, 800, 4, 900, 100], rows.Select(r => r.Pid));
    }

    [Fact]
    public void Descendants_respect_pid_reuse()
    {
        Assert.Equal([600, 700, 601, 602], ProcessTree.Descendants(Sample(), 500));
        Assert.Empty(ProcessTree.Descendants(Sample(), 900)); // 800 claims 900 as parent but predates it
    }

    [Fact]
    public void Cycles_never_hide_processes()
    {
        var rows = ProcessTree.Flatten([new(1, 2, T0, "a"), new(2, 1, T0, "b")]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Rate_tracker_reports_per_second_rates()
    {
        var r = new RateTracker<int>();
        Assert.Equal(0, r.Update(1, 1000, TimeSpan.FromSeconds(1).Ticks));
        Assert.Equal(500, r.Update(1, 2000, TimeSpan.FromSeconds(3).Ticks));
        r.Retain(new HashSet<int>());
        Assert.Equal(0, r.Update(1, 5000, TimeSpan.FromSeconds(4).Ticks));
    }
}
