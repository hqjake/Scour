using System.Text;

namespace Scour.Core.Shortcuts;

public sealed record LnkInfo(
    string? TargetPath,
    string? RelativePath,
    string? WorkingDirectory,
    string? Arguments,
    string? IconLocation,
    string? EnvironmentTarget)
{
    /// <summary>Every path-like field, for "does this shortcut point into folder X" checks.</summary>
    public IEnumerable<string> AllPaths()
    {
        if (!string.IsNullOrEmpty(TargetPath)) yield return TargetPath;
        if (!string.IsNullOrEmpty(EnvironmentTarget)) yield return EnvironmentTarget;
        if (!string.IsNullOrEmpty(WorkingDirectory)) yield return WorkingDirectory;
        if (!string.IsNullOrEmpty(IconLocation)) yield return IconLocation;
    }
}

/// <summary>
/// Minimal reader for Windows Shell Link (.lnk) files (MS-SHLLINK). Extracts the target path
/// without needing COM, so it works off-Windows and on shortcuts whose target is gone.
/// </summary>
public static class LnkParser
{
    private const uint HasLinkTargetIdList = 0x01;
    private const uint HasLinkInfo = 0x02;
    private const uint HasName = 0x04;
    private const uint HasRelativePath = 0x08;
    private const uint HasWorkingDir = 0x10;
    private const uint HasArguments = 0x20;
    private const uint HasIconLocation = 0x40;
    private const uint IsUnicode = 0x80;
    private const uint EnvironmentVariableBlock = 0xA0000001;

    public static LnkInfo? TryParse(byte[] data)
    {
        try { return Parse(data); }
        catch { return null; }
    }

    public static LnkInfo Parse(byte[] data)
    {
        if (data.Length < 0x4C || BitConverter.ToUInt32(data, 0) != 0x4C)
            throw new FormatException("Not a shell link file.");

        uint flags = BitConverter.ToUInt32(data, 0x14);
        int pos = 0x4C;

        if ((flags & HasLinkTargetIdList) != 0)
        {
            int idListSize = BitConverter.ToUInt16(data, pos);
            pos += 2 + idListSize;
        }

        string? target = null;
        if ((flags & HasLinkInfo) != 0)
        {
            int infoStart = pos;
            int infoSize = (int)BitConverter.ToUInt32(data, pos);
            int headerSize = (int)BitConverter.ToUInt32(data, pos + 4);
            uint infoFlags = BitConverter.ToUInt32(data, pos + 8);
            int localBaseOffset = (int)BitConverter.ToUInt32(data, pos + 16);
            int suffixOffset = (int)BitConverter.ToUInt32(data, pos + 24);

            if ((infoFlags & 0x1) != 0)
            {
                string? basePath = null, suffix = null;
                if (headerSize >= 0x24)
                {
                    int uBase = (int)BitConverter.ToUInt32(data, pos + 28);
                    int uSuffix = (int)BitConverter.ToUInt32(data, pos + 32);
                    if (uBase > 0) basePath = ReadNullTerminatedUnicode(data, infoStart + uBase);
                    if (uSuffix > 0) suffix = ReadNullTerminatedUnicode(data, infoStart + uSuffix);
                }
                basePath ??= ReadNullTerminatedAnsi(data, infoStart + localBaseOffset);
                suffix ??= suffixOffset > 0 ? ReadNullTerminatedAnsi(data, infoStart + suffixOffset) : "";
                target = string.IsNullOrEmpty(suffix) ? basePath : basePath.TrimEnd('\\') + "\\" + suffix;
            }
            pos = infoStart + infoSize;
        }

        bool unicode = (flags & IsUnicode) != 0;
        string? ReadStringData(uint flag)
        {
            if ((flags & flag) == 0) return null;
            int count = BitConverter.ToUInt16(data, pos);
            pos += 2;
            int bytes = unicode ? count * 2 : count;
            var s = unicode ? Encoding.Unicode.GetString(data, pos, bytes) : Encoding.Latin1.GetString(data, pos, bytes);
            pos += bytes;
            return s;
        }

        ReadStringData(HasName);
        var relative = ReadStringData(HasRelativePath);
        var workingDir = ReadStringData(HasWorkingDir);
        var args = ReadStringData(HasArguments);
        var icon = ReadStringData(HasIconLocation);

        string? envTarget = null;
        while (pos + 8 <= data.Length)
        {
            int blockSize = (int)BitConverter.ToUInt32(data, pos);
            if (blockSize < 4) break;
            uint signature = BitConverter.ToUInt32(data, pos + 4);
            if (signature == EnvironmentVariableBlock && blockSize >= 0x314)
            {
                envTarget = ReadNullTerminatedUnicode(data, pos + 8 + 260);
                if (string.IsNullOrEmpty(envTarget)) envTarget = ReadNullTerminatedAnsi(data, pos + 8);
            }
            pos += blockSize;
        }

        return new LnkInfo(target, relative, workingDir, args, icon, string.IsNullOrEmpty(envTarget) ? null : envTarget);
    }

    private static string ReadNullTerminatedAnsi(byte[] data, int offset)
    {
        int end = offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.Latin1.GetString(data, offset, end - offset);
    }

    private static string ReadNullTerminatedUnicode(byte[] data, int offset)
    {
        int end = offset;
        while (end + 1 < data.Length && (data[end] != 0 || data[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(data, offset, end - offset);
    }
}
