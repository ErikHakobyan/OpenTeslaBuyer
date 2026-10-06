using CommunityToolkit.Mvvm.ComponentModel;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>A labelled value shown in a card; updated in place so the UI does not flicker.</summary>
public sealed partial class Metric(string label, string? hint = null) : ObservableObject
{
    public string Label { get; } = label;

    public string? Hint { get; } = hint;

    [ObservableProperty]
    private string _value = Display.Missing;

    [ObservableProperty]
    private string? _detail;
}

/// <summary>One cell group (brick) in the cell-voltage chart.</summary>
public sealed partial class BrickBar(int index) : ObservableObject
{
    public const double MinHeight = 8;
    public const double MaxHeight = 110;

    public int Index { get; } = index;

    [ObservableProperty]
    private double _height = MinHeight;

    [ObservableProperty]
    private bool _isLowest;

    [ObservableProperty]
    private bool _isHighest;

    [ObservableProperty]
    private string _toolTip = "";
}
