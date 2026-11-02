using CommunityToolkit.Mvvm.ComponentModel;
using Monolith.Tweaks;

namespace Monolith.Modules.Settings;

public sealed record IntervalOption(int Milliseconds, string Label, string Hint);

/// <summary>A file or folder Lycoris keeps its data in.</summary>
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

/// <summary>One row of «Что Lycoris не делает и почему»: the rejected tweak plus the transient palette highlight.</summary>
public sealed partial class RejectedRow(RejectedTweak tweak) : ObservableObject
{
    public RejectedTweak Tweak { get; } = tweak;
    public string Id => Tweak.Id;
    // Shown texts: translated when the Tweaks tables know the Russian original (otherwise as written).
    public string Title => Monolith.UI.SentenceCaseConverter.Apply(Loc.Sink(Tweak.Title));
    public string WhatOthersDo => Loc.Sink(Tweak.WhatOthersDo);
    public string WhyNot => Loc.Sink(Tweak.WhyNot);
    public string Alternative => Loc.Sink(Tweak.Alternative);
    [ObservableProperty] private bool _isHighlighted;
}
