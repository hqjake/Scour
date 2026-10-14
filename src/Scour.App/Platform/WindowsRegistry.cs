using System.IO;
using System.Security;
using Microsoft.Win32;
using Scour.Core.Platform;

namespace Scour.App.Platform;

/// <summary><see cref="IRegistry"/> over the real registry, always in the native 64-bit view.</summary>
public sealed class WindowsRegistry : IRegistry
{
    private readonly Dictionary<RegHive, RegistryKey> _bases = new()
    {
        [RegHive.LocalMachine] = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64),
        [RegHive.CurrentUser] = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64),
        [RegHive.ClassesRoot] = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        [RegHive.Users] = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
    };

    private RegistryKey? Open(RegKeyPath path, bool writable = false)
    {
        try
        {
            var b = _bases[path.Hive];
            return path.SubPath.Length == 0 ? b : b.OpenSubKey(path.SubPath, writable);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private void Release(RegKeyPath path, RegistryKey key)
    {
        if (path.SubPath.Length != 0) key.Dispose();
    }

    public bool KeyExists(RegKeyPath key)
    {
        var k = Open(key);
        if (k is null) return false;
        Release(key, k);
        return true;
    }

    public IReadOnlyList<string> GetSubKeyNames(RegKeyPath key)
    {
        var k = Open(key);
        if (k is null) return [];
        try { return k.GetSubKeyNames(); }
        catch { return []; }
        finally { Release(key, k); }
    }

    public IReadOnlyList<string> GetValueNames(RegKeyPath key)
    {
        var k = Open(key);
        if (k is null) return [];
        try { return k.GetValueNames(); }
        catch { return []; }
        finally { Release(key, k); }
    }

    public RegValue? GetValue(RegKeyPath key, string name)
    {
        var k = Open(key);
        if (k is null) return null;
        try
        {
            var data = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (data is null) return null;
            var kind = k.GetValueKind(name);
            return kind switch
            {
                RegistryValueKind.String => RegValue.String((string)data),
                RegistryValueKind.ExpandString => RegValue.Expand((string)data),
                RegistryValueKind.MultiString => RegValue.Multi((string[])data),
                RegistryValueKind.DWord => RegValue.DWord(unchecked((uint)(int)data)),
                RegistryValueKind.QWord => RegValue.QWord(unchecked((ulong)(long)data)),
                RegistryValueKind.Binary => RegValue.Binary((byte[])data),
                _ => new RegValue(RegValueKind.None, data as byte[] ?? []),
            };
        }
        catch
        {
            return null;
        }
        finally { Release(key, k); }
    }

    public void CreateKey(RegKeyPath key)
    {
        _bases[key.Hive].CreateSubKey(key.SubPath, writable: true)?.Dispose();
    }

    public void SetValue(RegKeyPath key, string name, RegValue value)
    {
        using var k = _bases[key.Hive].CreateSubKey(key.SubPath, writable: true)
                      ?? throw new IOException($"Cannot open {key} for writing.");
        switch (value.Kind)
        {
            case RegValueKind.String: k.SetValue(name, (string)value.Data, RegistryValueKind.String); break;
            case RegValueKind.ExpandString: k.SetValue(name, (string)value.Data, RegistryValueKind.ExpandString); break;
            case RegValueKind.MultiString: k.SetValue(name, (string[])value.Data, RegistryValueKind.MultiString); break;
            case RegValueKind.DWord: k.SetValue(name, unchecked((int)Convert.ToUInt32(value.Data)), RegistryValueKind.DWord); break;
            case RegValueKind.QWord: k.SetValue(name, unchecked((long)Convert.ToUInt64(value.Data)), RegistryValueKind.QWord); break;
            case RegValueKind.Binary: k.SetValue(name, (byte[])value.Data, RegistryValueKind.Binary); break;
            default: k.SetValue(name, value.Data as byte[] ?? [], RegistryValueKind.None); break;
        }
    }

    public void DeleteKeyTree(RegKeyPath key)
    {
        if (key.Parent is not { } parent) throw new InvalidOperationException("Refusing to delete a registry hive.");
        using var p = Open(parent, writable: true) ?? throw new IOException($"Cannot open {parent} for writing.");
        p.DeleteSubKeyTree(key.Name, throwOnMissingSubKey: false);
    }

    public void DeleteValue(RegKeyPath key, string name)
    {
        var k = Open(key, writable: true) ?? throw new IOException($"Cannot open {key} for writing.");
        try { k.DeleteValue(name, throwOnMissingValue: false); }
        finally { Release(key, k); }
    }
}
