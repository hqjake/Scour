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
        [RegHive.CurrentUser] = CurrentUserHive(),
        [RegHive.ClassesRoot] = RegistryKey.OpenBaseKey(RegistryHive.ClassesRoot, RegistryView.Registry64),
        [RegHive.Users] = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64),
    };

    /// <summary>
    /// HKCU — of the account signed in to the desktop when Scour was elevated with another administrator's
    /// password (see <see cref="SessionUser"/>), so startup entries, privacy settings and per-user programs are theirs.
    /// </summary>
    private static RegistryKey CurrentUserHive()
    {
        if (SessionUser.Other is { } user)
        {
            try
            {
                var hive = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64).OpenSubKey(user.Sid, writable: true);
                if (hive is not null) return hive;
            }
            catch { /* fall back to Scour's own account */ }
        }
        return RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
    }

    private RegistryKey? Open(RegKeyPath path, bool writable = false)
    {
        try
        {
            var b = _bases[path.Hive];
            return path.SubPath.Length == 0 ? b : b.OpenSubKey(path.SubPath, writable);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException or IOException
                                      or ArgumentException or ObjectDisposedException)
        {
            // ArgumentException: a key name over 255 characters, built from registry data.
            return null;
        }
    }

    /// <summary>Null only if the key doesn't exist; access denied throws (unlike <see cref="Open"/>), so a
    /// protected key is reported as not deleted rather than as already gone.</summary>
    private RegistryKey? OpenForWriting(RegKeyPath path)
    {
        var b = _bases[path.Hive];
        if (path.SubPath.Length == 0) return b;
        try { return b.OpenSubKey(path.SubPath, writable: true); }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            throw new IOException($"Access to {path} was denied.", e);
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
        var p = OpenForWriting(parent);
        if (p is null) return; // nothing to delete, same as a missing subkey
        try { p.DeleteSubKeyTree(key.Name, throwOnMissingSubKey: false); }
        finally { Release(parent, p); }
    }

    public void DeleteValue(RegKeyPath key, string name)
    {
        // A missing key has no values to delete (e.g. re-enabling a context-menu entry when the "Blocked"
        // key was never created).
        var k = OpenForWriting(key);
        if (k is null) return;
        try { k.DeleteValue(name, throwOnMissingValue: false); }
        finally { Release(key, k); }
    }
}
