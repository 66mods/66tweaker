using System.Windows;
using System.Windows.Controls;

namespace Tweaker.App.Views;

public partial class GamesView : UserControl
{
    public GamesView()
    {
        InitializeComponent();
    }
}

/// <summary>The stock converter, exposed as one shared instance so XAML can reference it with x:Static.</summary>
public static class BooleanToVisibilityConverterInstance
{
    public static BooleanToVisibilityConverter Instance { get; } = new();
}
