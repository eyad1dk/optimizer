using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ForgePC;

// Presentation mappings do not participate in operation planning or execution.
public sealed class PresentationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string text = value?.ToString() ?? "";
        return parameter?.ToString() switch
        {
            "RailWidth" => new GridLength(value is double width && width > 0 && width < 960 ? 72 : 216),
            "RailLabel" => value is double width && width > 0 && width < 960 ? Visibility.Collapsed : Visibility.Visible,
            "QueueHeight" => value is double height ? Math.Min(218, Math.Max(88, height * 0.34)) : 218d,
            "Glyph" => text switch
            {
                "Overview" => "\uE80F", "CPU"=>"\uE950", "GPU"=>"\uE7F4", "Memory"=>"\uE964", "Network"=>"\uE839", "Storage"=>"\uEDA2", "Startup"=>"\uE7B5", "Services"=>"\uE713", "Windows"=>"\uE782", "Privacy"=>"\uE72E", "Power"=>"\uE7E8", "Cleanup"=>"\uE74D", "Optimize" => "\uE9E9",
                "Profiles" => "\uE771", "Diagnostics" => "\uE9D9", "Recovery & History" => "\uE81C",
                "Settings" => "\uE713", "Gaming" => "\uE7FC", "Coding" => "\uE943",
                "Everyday" => "\uE8A5", "Quiet" => "\uE708", "Battery Saver" => "\uEBA0", _ => "\uE946"
            },
            "NavTitle" => text == "Recovery & History" ? "Recovery" : text,
            "Subtitle" => text switch
            {
                "Overview" => "A clear view of your PC. A thoughtful way to tune it.",
                "Optimize" => "Small adjustments. Clear tradeoffs. You're in control.",
                "Profiles" => "Find your focus. Choose a setup that fits your day.",
                "Diagnostics" => "Get to know what's running under the surface.",
                "Recovery & History" => "Every change has a story. Pick up where you left off.",
                "Settings" => "Make EZoptimizer feel at home.", _ => ""
            },
            "ProfileDescription" => text switch
            {
                "Gaming" => "Settle in for your next session.", "Coding" => "A calmer setup for deep work.",
                "Everyday" => "Keep things familiar and balanced.", "Quiet" => "Ease back on power use.",
                "Battery Saver" => "A conservative setup on the go.", _ => "Your saved preferences."
            },
            "ProfileDetail" => text switch
            {
                "Gaming" or "Coding" => "Reduced window and menu animations · Balanced power plan",
                "Balanced" or "Everyday" => "Window and menu animations on · Balanced power plan",
                "Quiet" or "Battery Saver" => "Reduced window and menu animations · Installed Power saver plan",
                "Maximum Performance" => "Installed High performance plan. Higher heat and power use; blocked on battery or unknown AC state.",
                "Competitive Gaming" or "Low-End PC" => "Reduced desktop effects and Balanced power. No game settings or services are changed.",
                "Custom" => "Start with an empty queue and choose individual controls in Optimize.",
                _ => "Review your imported settings before applying."
            },
            _ => text
        };
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

// Cards preserve their reading order while flowing to fewer columns in smaller windows.
public sealed class AdaptivePanel : Panel
{
    public static readonly DependencyProperty MinimumItemWidthProperty = DependencyProperty.Register(nameof(MinimumItemWidth), typeof(double), typeof(AdaptivePanel), new FrameworkPropertyMetadata(220d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(nameof(MaxColumns), typeof(int), typeof(AdaptivePanel), new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(nameof(Gap), typeof(double), typeof(AdaptivePanel), new FrameworkPropertyMetadata(14d, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinimumItemWidth { get => (double)GetValue(MinimumItemWidthProperty); set => SetValue(MinimumItemWidthProperty, value); }
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }
    public double Gap { get => (double)GetValue(GapProperty); set => SetValue(GapProperty, value); }
    private int Columns(double width) => Math.Max(1, Math.Min(Math.Max(1, MaxColumns), (int)((width + Gap) / (Math.Max(1, MinimumItemWidth) + Gap))));
    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? Math.Max(1, MaxColumns) * (MinimumItemWidth + Gap) - Gap : available.Width;
        int columns = Columns(width); double itemWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns), height = 0;
        for (int start = 0; start < InternalChildren.Count; start += columns)
        {
            double rowHeight = 0;
            for (int index = start; index < Math.Min(start + columns, InternalChildren.Count); index++) { InternalChildren[index].Measure(new Size(itemWidth, double.PositiveInfinity)); rowHeight = Math.Max(rowHeight, InternalChildren[index].DesiredSize.Height); }
            height += rowHeight + (start == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size final)
    {
        int columns = Columns(final.Width); double itemWidth = Math.Max(0, (final.Width - Gap * (columns - 1)) / columns), top = 0;
        for (int start = 0; start < InternalChildren.Count; start += columns)
        {
            double rowHeight = 0;
            for (int index = start; index < Math.Min(start + columns, InternalChildren.Count); index++) rowHeight = Math.Max(rowHeight, InternalChildren[index].DesiredSize.Height);
            for (int index = start; index < Math.Min(start + columns, InternalChildren.Count); index++) InternalChildren[index].Arrange(new Rect((index - start) * (itemWidth + Gap), top, itemWidth, rowHeight));
            top += rowHeight + Gap;
        }
        return final;
    }
}