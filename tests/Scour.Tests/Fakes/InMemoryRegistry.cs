using Scour.Core.Platform;

namespace Scour.Tests.Fakes;

public sealed class InMemoryRegistry : IRegistry
{
    private sealed class Node
    {
        public readonly Dictionary<string, Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, RegValue> Values = new(StringComparer.OrdinalIgnoreCase);
        public string Name = "";
    }

    private readonly Dictionary<RegHive, Node> _hives = new();

    private Node? Find(RegKeyPath key, bool create)
    {
        if (!_hives.TryGetValue(key.Hive, out var node))
        {
            if (!create) return null;
            _hives[key.Hive] = node = new Node();
        }
        if (key.SubPath.Length == 0) return node;
        foreach (var part in key.SubPath.Split('\\'))
        {
            if (!node.Children.TryGetValue(part, out var child))
            {
                if (!create) return null;
                node.Children[part] = child = new Node { Name = part };
            }
            node = child;
        }
        return node;
    }

    public bool KeyExists(RegKeyPath key) => Find(key, false) is not null;
    public IReadOnlyList<string> GetSubKeyNames(RegKeyPath key) => Find(key, false)?.Children.Values.Select(c => c.Name).ToList() ?? [];
    public IReadOnlyList<string> GetValueNames(RegKeyPath key) => Find(key, false)?.Values.Keys.ToList() ?? [];
    public RegValue? GetValue(RegKeyPath key, string name) => Find(key, false)?.Values.GetValueOrDefault(name);
    public void CreateKey(RegKeyPath key) => Find(key, true);
    public void SetValue(RegKeyPath key, string name, RegValue value) => Find(key, true)!.Values[name] = value;

    public void DeleteKeyTree(RegKeyPath key)
    {
        if (key.Parent is not { } parent) return;
        Find(parent, false)?.Children.Remove(key.Name);
    }

    public void DeleteValue(RegKeyPath key, string name) => Find(key, false)?.Values.Remove(name);

    // Convenience for test setup.
    public InMemoryRegistry Set(string path, string name, string value)
    {
        SetValue(RegKeyPath.Parse(path), name, RegValue.String(value));
        return this;
    }

    public InMemoryRegistry Set(string path, string name, RegValue value)
    {
        SetValue(RegKeyPath.Parse(path), name, value);
        return this;
    }

    public InMemoryRegistry Key(string path)
    {
        CreateKey(RegKeyPath.Parse(path));
        return this;
    }

    public bool Has(string path) => KeyExists(RegKeyPath.Parse(path));
}
