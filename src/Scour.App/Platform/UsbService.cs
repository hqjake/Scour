using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Scour.Core.Usb;

namespace Scour.App.Platform;

public sealed record UsbDisk(int Number, string Name, long Size, string BusType, string PartitionStyle, string Letters, string Serial, int SectorSize)
{
    public string Display => $"Disk {Number}: {Name} — {Services.Format.Bytes(Size)}" + (Letters.Length > 0 ? $"  ({Letters})" : "");
}

public sealed record PreparedPartition(char Letter, long Offset, long Size, int PartitionNumber, int SectorSize);

/// <summary>
/// Partitions, formats and writes USB drives. Every destructive call re-checks that the target
/// is still the same removable, non-system disk.
/// </summary>
public static class UsbService
{
    private const string AllowedBus = "'USB','SD','MMC'";

    public static async Task<List<UsbDisk>> ListAsync()
    {
        var script =
            "Get-Disk | Where-Object { (\"$($_.BusType)\" -in " + AllowedBus + ") -and -not $_.IsBoot -and -not $_.IsSystem } | ForEach-Object { " +
            "$letters = (Get-Partition -DiskNumber $_.Number -ErrorAction SilentlyContinue | Where-Object { $_.DriveLetter } | ForEach-Object { \"$($_.DriveLetter):\" }) -join ' '; " +
            "[pscustomobject]@{ Number = $_.Number; Name = $_.FriendlyName; Size = $_.Size; Bus = \"$($_.BusType)\"; Style = \"$($_.PartitionStyle)\"; " +
            "Letters = $letters; Serial = \"$($_.SerialNumber)\"; Sector = $_.LogicalSectorSize } } | ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(1));
        if (result.ExitCode != 0) throw new InvalidOperationException(LastLine(result.Output));
        var list = new List<UsbDisk>();
        foreach (var e in JsonItems(result.StdOut))
            list.Add(new UsbDisk(e.GetProperty("Number").GetInt32(), Str(e, "Name"), e.GetProperty("Size").GetInt64(), Str(e, "Bus"),
                                 Str(e, "Style"), Str(e, "Letters"), Str(e, "Serial").Trim(), e.TryGetProperty("Sector", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 512));
        return list;
    }

    /// <summary>Guards against the stick being swapped between picking it and erasing it.</summary>
    public static async Task VerifyAsync(UsbDisk disk)
    {
        var now = (await ListAsync()).FirstOrDefault(d => d.Number == disk.Number);
        if (now is null || now.Name != disk.Name || now.Size != disk.Size)
            throw new InvalidOperationException("The selected USB drive changed or was unplugged. Refresh the list and pick it again.");
    }

    /// <summary>Wipes the partition table and creates one partition spanning the drive.</summary>
    public static async Task<PreparedPartition> PrepareAsync(UsbDisk disk, PartitionScheme scheme, bool fat32)
    {
        await VerifyAsync(disk);
        var style = scheme == PartitionScheme.Gpt ? "GPT" : "MBR";
        var partitionArgs = scheme == PartitionScheme.Gpt
            ? "-GptType '{ebd0a0a2-b9e5-4433-87c0-68b6b72699c7}'"
            : "-IsActive -MbrType " + (fat32 ? "FAT32XINT13" : "IFS");
        var script = $@"
$ErrorActionPreference = 'Stop'
$n = {disk.Number}
$d = Get-Disk -Number $n
if ($d.IsBoot -or $d.IsSystem -or (""$($d.BusType)"" -notin {AllowedBus})) {{ throw 'Refusing to touch a disk that is not a removable USB drive.' }}
if ($d.IsOffline) {{ Set-Disk -Number $n -IsOffline $false }}
if ($d.IsReadOnly) {{ Set-Disk -Number $n -IsReadOnly $false }}
if (""$($d.PartitionStyle)"" -ne 'RAW') {{ Clear-Disk -Number $n -RemoveData -RemoveOEM -Confirm:$false }}
$d = Get-Disk -Number $n
if (""$($d.PartitionStyle)"" -eq 'RAW') {{ Initialize-Disk -Number $n -PartitionStyle {style} }}
elseif (""$($d.PartitionStyle)"" -ne '{style}') {{ Set-Disk -Number $n -PartitionStyle {style} }}
$p = New-Partition -DiskNumber $n -UseMaximumSize -AssignDriveLetter {partitionArgs}
Start-Sleep -Seconds 2
$p = Get-Partition -DiskNumber $n -PartitionNumber $p.PartitionNumber
[pscustomobject]@{{ Letter = ""$($p.DriveLetter)""; Offset = $p.Offset; Size = $p.Size; Number = $p.PartitionNumber; Sector = $d.LogicalSectorSize }} | ConvertTo-Json -Compress";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromMinutes(5));
        if (result.ExitCode != 0) throw new InvalidOperationException("Partitioning failed: " + LastLine(result.Output));
        var e = JsonItems(result.StdOut).Single();
        var letter = Str(e, "Letter");
        if (letter.Length == 0 || letter[0] == '\0') throw new InvalidOperationException("Windows didn't assign a drive letter to the new partition.");
        return new PreparedPartition(letter[0], e.GetProperty("Offset").GetInt64(), e.GetProperty("Size").GetInt64(),
                                     e.GetProperty("Number").GetInt32(), e.GetProperty("Sector").GetInt32());
    }

    public static async Task FormatVolumeAsync(char letter, string fileSystem, string label, int? clusterBytes, bool full)
    {
        var safeLabel = label.Replace("'", "''");
        var cluster = clusterBytes is { } c ? $"-AllocationUnitSize {c}" : "";
        var script = $"$ErrorActionPreference = 'Stop'; Format-Volume -DriveLetter {letter} -FileSystem {fileSystem} -NewFileSystemLabel '{safeLabel}' {cluster} " +
                     (full ? "-Full " : "") + "-Force -Confirm:$false | Out-Null";
        var (code, output) = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromHours(8));
        if (code != 0) throw new InvalidOperationException("Formatting failed: " + LastLine(output));
    }

    // ---- raw access

    private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    private const uint FSCTL_LOCK_VOLUME = 0x00090018, FSCTL_UNLOCK_VOLUME = 0x0009001C, FSCTL_DISMOUNT_VOLUME = 0x00090020;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    private static SafeFileHandle Open(string path)
    {
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return h;
    }

    /// <summary>Writes Scour's own FAT32 file system onto a partition (works above Windows' 32 GB limit).</summary>
    public static void WriteFat32(PreparedPartition partition, string label, IProgress<double>? progress, CancellationToken ct)
    {
        var layout = Fat32Formatter.Plan(partition.Size, partition.SectorSize);
        using var handle = Open($@"\\.\{partition.Letter}:");
        if (!DeviceIoControl(handle, FSCTL_LOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new InvalidOperationException("The drive is in use. Close any windows or programs using it and try again.");
        DeviceIoControl(handle, FSCTL_DISMOUNT_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        try
        {
            using var stream = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0);
            Fat32Formatter.Format(stream, layout, (uint)(partition.Offset / partition.SectorSize), label, (uint)Random.Shared.Next(), progress, ct);
        }
        finally
        {
            DeviceIoControl(handle, FSCTL_UNLOCK_VOLUME, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        }
    }

    /// <summary>Writes an image byte-for-byte to the whole disk ("DD mode").</summary>
    public static async Task WriteRawAsync(UsbDisk disk, string imagePath, IProgress<double> progress, CancellationToken ct)
    {
        await VerifyAsync(disk);
        // Remove the existing partitions so no mounted volume blocks raw writes.
        var (code, output) = await ProcessRunner.PowerShellAsync(
            $"$ErrorActionPreference = 'Stop'; $d = Get-Disk -Number {disk.Number}; " +
            $"if ($d.IsBoot -or $d.IsSystem -or (\"$($d.BusType)\" -notin {AllowedBus})) {{ throw 'Refusing.' }}; " +
            $"if (\"$($d.PartitionStyle)\" -ne 'RAW') {{ Clear-Disk -Number {disk.Number} -RemoveData -RemoveOEM -Confirm:$false }}",
            TimeSpan.FromMinutes(5), ct);
        if (code != 0) throw new InvalidOperationException("Could not clear the drive: " + LastLine(output));

        await Task.Run(() =>
        {
            int sector = Math.Max(512, disk.SectorSize);
            using var handle = Open($@"\\.\PhysicalDrive{disk.Number}");
            using var target = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0);
            using var source = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, System.IO.FileOptions.SequentialScan);
            long total = source.Length, done = 0;
            var buffer = new byte[4 << 20];
            int read;
            while ((read = source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                int padded = (read + sector - 1) / sector * sector; // raw disk writes must be whole sectors
                if (padded > read) Array.Clear(buffer, read, padded - read);
                target.Write(buffer, 0, padded);
                done += read;
                progress.Report((double)done / total);
            }
            target.Flush();
        }, ct);
        await ProcessRunner.PowerShellAsync($"Update-Disk -Number {disk.Number}", TimeSpan.FromMinutes(1));
    }

    // ---- ISO handling

    public static async Task<char> MountIsoAsync(string isoPath)
    {
        var path = isoPath.Replace("'", "''");
        var result = await ProcessRunner.PowerShellAsync(
            $"$ErrorActionPreference = 'Stop'; $img = Mount-DiskImage -ImagePath '{path}' -PassThru; Start-Sleep -Milliseconds 800; " +
            "($img | Get-Volume).DriveLetter", TimeSpan.FromMinutes(2));
        var letter = result.StdOut.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length == 1 && char.IsAsciiLetter(l[0])) ?? "";
        if (result.ExitCode != 0 || letter.Length == 0) throw new InvalidOperationException("Could not open the ISO: " + LastLine(result.Output));
        return letter[0];
    }

    public static Task DismountIsoAsync(string isoPath) =>
        ProcessRunner.PowerShellAsync($"Dismount-DiskImage -ImagePath '{isoPath.Replace("'", "''")}' | Out-Null", TimeSpan.FromMinutes(1));

    public static List<(string Path, long Size)> ListFiles(string root)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return Directory.EnumerateFiles(root, "*", opts)
            .Select(f => (Path.GetRelativePath(root, f), new FileInfo(f).Length))
            .ToList();
    }

    public static async Task CopyFilesAsync(string sourceRoot, string targetRoot, IReadOnlyList<(string Path, long Size)> files,
                                            ISet<string> skip, IProgress<(long Done, long Total, string File)> progress, CancellationToken ct)
    {
        long total = files.Where(f => !skip.Contains(f.Path)).Sum(f => f.Size), done = 0;
        await Task.Run(() =>
        {
            var buffer = new byte[4 << 20];
            foreach (var (rel, _) in files)
            {
                if (skip.Contains(rel)) continue;
                ct.ThrowIfCancellationRequested();
                var dest = Path.Combine(targetRoot, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var src = new FileStream(Path.Combine(sourceRoot, rel), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, System.IO.FileOptions.SequentialScan);
                using var dst = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                int n;
                while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    dst.Write(buffer, 0, n);
                    done += n;
                    progress.Report((done, total, rel));
                }
                File.SetAttributes(dest, FileAttributes.Normal);
            }
        }, ct);
    }

    public static async Task SplitWimAsync(char isoLetter, string wimRelative, char target, CancellationToken ct)
    {
        var swm = Path.ChangeExtension(Path.Combine($"{target}:\\", wimRelative), ".swm");
        Directory.CreateDirectory(Path.GetDirectoryName(swm)!);
        var (code, output) = await ProcessRunner.RunHiddenAsync("dism.exe",
            $"/Split-Image /ImageFile:\"{isoLetter}:\\{wimRelative}\" /SWMFile:\"{swm}\" /FileSize:3800", TimeSpan.FromHours(2), ct);
        if (code != 0) throw new InvalidOperationException("Splitting install.wim failed: " + LastLine(output));
    }

    /// <summary>Writes the Windows boot code to the stick so legacy BIOS PCs can start setup.</summary>
    public static async Task<bool> WriteBiosBootCodeAsync(char isoLetter, char target)
    {
        var bootsect = $@"{isoLetter}:\boot\bootsect.exe";
        if (!File.Exists(bootsect)) return false;
        var (code, _) = await ProcessRunner.RunHiddenAsync(bootsect, $"/nt60 {target}: /mbr /force", TimeSpan.FromMinutes(2));
        return code == 0;
    }

    private static IEnumerable<JsonElement> JsonItems(string json)
    {
        json = json.Trim();
        if (json.Length == 0) yield break;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
            foreach (var e in doc.RootElement.EnumerateArray()) yield return e.Clone();
        else
            yield return doc.RootElement.Clone();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";

    private static string LastLine(string s) =>
        s.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0 && !l.StartsWith("At line", StringComparison.Ordinal) && !l.StartsWith('+')) ?? "unknown error";
}
