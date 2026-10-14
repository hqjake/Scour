using CommunityToolkit.Mvvm.ComponentModel;
using Scour.App.Services;
using Scour.Core.Leftovers;

namespace Scour.App.ViewModels;

public sealed partial class LeftoverItemViewModel(LeftoverItem item) : ObservableObject
{
    public LeftoverItem Item { get; } = item;

    [ObservableProperty]
    private bool _isChecked = item.Confidence == Confidence.Safe;

    public string GroupName => Item.IsRegistry ? "Registry" : "Files and folders";

    public string Primary => Item.Kind == LeftoverKind.RegistryValue
        ? (string.IsNullOrEmpty(Item.ValueName) ? "(Default)" : Item.ValueName!)
        : Item.Path;

    public string? Secondary => Item.Kind == LeftoverKind.RegistryValue ? "in " + Item.Path : null;

    public string Reasons => string.Join("\n", Item.Reasons);

    public string KindText => Item.Kind switch
    {
        LeftoverKind.RegistryKey => Item.ServiceName is null ? "Key" : "Service",
        LeftoverKind.RegistryValue => "Value",
        LeftoverKind.Folder => "Folder",
        _ => "File",
    };

    /// <summary>Segoe Fluent Icons glyph.</summary>
    public string Glyph => Item.Kind switch
    {
        LeftoverKind.RegistryKey => "",
        LeftoverKind.RegistryValue => "",
        LeftoverKind.Folder => "",
        _ => "",
    };

    public Confidence Confidence => Item.Confidence;
    public string ConfidenceText => Item.Confidence.ToString();
    public string SizeText => Format.Bytes(Item.SizeBytes);
}
