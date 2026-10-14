namespace Scour.Core.Platform;

/// <summary>
/// Minimal registry surface used by the engine. Read methods never throw for missing keys
/// (they return empty results); access-denied keys are treated as missing.
/// </summary>
public interface IRegistry
{
    bool KeyExists(RegKeyPath key);
    IReadOnlyList<string> GetSubKeyNames(RegKeyPath key);
    IReadOnlyList<string> GetValueNames(RegKeyPath key);

    /// <param name="name">Value name; the empty string is the key's default value.</param>
    RegValue? GetValue(RegKeyPath key, string name);

    void CreateKey(RegKeyPath key);
    void SetValue(RegKeyPath key, string name, RegValue value);
    void DeleteKeyTree(RegKeyPath key);
    void DeleteValue(RegKeyPath key, string name);
}

public static class RegistryExtensions
{
    public static string? GetString(this IRegistry reg, RegKeyPath key, string name) =>
        reg.GetValue(key, name)?.AsString();

    public static long? GetInteger(this IRegistry reg, RegKeyPath key, string name) =>
        reg.GetValue(key, name)?.AsInteger();

    public static bool ValueExists(this IRegistry reg, RegKeyPath key, string name) =>
        reg.GetValue(key, name) is not null;
}
