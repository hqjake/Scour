using System.Text;
using Scour.Core.Repair;
using Scour.Tests.Fakes;

namespace Scour.Tests;

public class RepairTests
{
    [Fact]
    public void Catalog_is_well_formed()
    {
        var f = TestFolders.Create();
        var actions = RepairCatalog.Create(f, @"C:\");
        Assert.Equal(actions.Count, actions.Select(a => a.Id).Distinct().Count());
        foreach (var a in actions)
        {
            Assert.NotEmpty(a.Steps);
            Assert.All(a.Steps, s => Assert.False(string.IsNullOrWhiteSpace(s.Path)));
            Assert.All(a.Steps.Where(s => s.Kind is RepairStepKind.Command or RepairStepKind.StartDetached),
                       s => Assert.StartsWith(f.WindowsDir, s.Path, StringComparison.OrdinalIgnoreCase));
        }
        // Folders are only ever renamed, never deleted, and only inside Windows.
        var renames = actions.SelectMany(a => a.Steps).Where(s => s.Kind == RepairStepKind.RenameFolder).ToList();
        Assert.Equal(2, renames.Count);
        Assert.All(renames, r => Assert.StartsWith(f.WindowsDir, r.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(@"C:\Windows\SoftwareDistribution.old-20261014-2130",
                     RepairCatalog.BackupName(@"C:\Windows\SoftwareDistribution", new DateTime(2026, 10, 14, 21, 30, 0)));
    }

    [Fact]
    public void Decodes_utf16_output_with_progress_redraws()
    {
        var d = new OutputDecoder();
        var text = "Beginning system scan.\r\n\r\nVerification 10% complete.\rVerification 10% complete.\rVerification 55% complete.\rVerification 100% complete.\r\nDone.\r\n";
        var bytes = Encoding.Unicode.GetBytes(text);
        var lines = new List<string>();
        // Feed in odd-sized chunks to exercise split characters.
        for (int i = 0; i < bytes.Length; i += 7) lines.AddRange(d.Feed(bytes.AsSpan(i, Math.Min(7, bytes.Length - i))));
        lines.AddRange(d.Flush());
        Assert.Equal(["Beginning system scan.", "Verification 10% complete.", "Verification 55% complete.", "Verification 100% complete.", "Done."], lines);
    }

    [Fact]
    public void Decodes_plain_output()
    {
        var d = new OutputDecoder();
        var lines = d.Feed(Encoding.UTF8.GetBytes("Windows IP Configuration\r\n\r\nSuccessfully flushed the DNS Resolver Cache.")).ToList();
        lines.AddRange(d.Flush());
        Assert.Equal(["Windows IP Configuration", "Successfully flushed the DNS Resolver Cache."], lines);
    }
}
