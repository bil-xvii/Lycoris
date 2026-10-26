using CommunityToolkit.Mvvm.ComponentModel;

namespace Monolith.Modules.Settings;

public sealed record IntervalOption(int Milliseconds, string Label, string Hint);

/// <summary>A file or folder MONOLITH keeps its data in.</summary>
public sealed partial class DataPathItem : ObservableObject
{
    public required string Title { get; init; }
    public required string Path { get; init; }
    public required string Description { get; init; }
    public bool IsFile { get; init; }
    [ObservableProperty] private string _state = "…";
    [ObservableProperty] private bool _exists;
}

public sealed record LicenseItem(string Name, string Version, string License, string Copyright, string Use);

public sealed record PrincipleItem(string Title, string Text);
