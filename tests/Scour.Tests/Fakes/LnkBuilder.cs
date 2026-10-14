using System.Text;

namespace Scour.Tests.Fakes;

/// <summary>Builds minimal but valid .lnk files for tests.</summary>
public static class LnkBuilder
{
    public static byte[] Build(string? targetPath, string? workingDir = null, string? arguments = null)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        uint flags = 0x80; // IsUnicode
        if (targetPath is not null) flags |= 0x02;
        if (workingDir is not null) flags |= 0x10;
        if (arguments is not null) flags |= 0x20;

        w.Write(0x4C);
        w.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        w.Write(flags);
        w.Write(new byte[0x4C - 0x18]);

        if (targetPath is not null)
        {
            var ansi = Encoding.Latin1.GetBytes(targetPath + "\0");
            const int headerSize = 0x1C;
            var volumeId = new byte[0x11];
            BitConverter.GetBytes(0x11).CopyTo(volumeId, 0);
            int volumeOffset = headerSize;
            int baseOffset = volumeOffset + volumeId.Length;
            int suffixOffset = baseOffset + ansi.Length;
            int total = suffixOffset + 1;
            w.Write(total);
            w.Write(headerSize);
            w.Write(1); // VolumeIDAndLocalBasePath
            w.Write(volumeOffset);
            w.Write(baseOffset);
            w.Write(0);
            w.Write(suffixOffset);
            w.Write(volumeId);
            w.Write(ansi);
            w.Write((byte)0);
        }

        void WriteString(string s)
        {
            w.Write((ushort)s.Length);
            w.Write(Encoding.Unicode.GetBytes(s));
        }
        if (workingDir is not null) WriteString(workingDir);
        if (arguments is not null) WriteString(arguments);
        w.Write(0); // terminal block
        return ms.ToArray();
    }
}
