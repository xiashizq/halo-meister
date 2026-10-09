using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HaloMeister.App.Controls;

/// <summary>
/// Lays children left-to-right and wraps to the next row when the line is full.
/// </summary>
public sealed class WrapPanel : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(
            nameof(HorizontalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(
            nameof(VerticalSpacing),
            typeof(double),
            typeof(WrapPanel),
            new PropertyMetadata(8.0, OnLayoutChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private double _arrangedWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = FiniteWidth(availableSize.Width);
        if (width <= 0)
            width = FiniteWidth(_arrangedWidth);
        return MeasureOrArrange(width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = FiniteWidth(finalSize.Width);
        if (width <= 0)
            width = FiniteWidth(_arrangedWidth);
        if (width > 0 && Math.Abs(width - _arrangedWidth) > 0.5)
        {
            _arrangedWidth = width;
            InvalidateMeasure();
        }
        return MeasureOrArrange(width, arrange: true);
    }

    private Size MeasureOrArrange(double width, bool arrange)
    {
        double x = 0;
        double y = 0;
        double rowHeight = 0;
        double usedWidth = 0;
        double limit = width > 0 ? width : double.PositiveInfinity;

        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            if (!arrange)
                child.Measure(new Size(limit, double.PositiveInfinity));

            Size size = child.DesiredSize;
            if (x > 0 && x + HorizontalSpacing + size.Width > limit)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            if (arrange)
                child.Arrange(new Rect(x, y, size.Width, size.Height));

            if (x > 0)
                x += HorizontalSpacing;
            x += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            usedWidth = Math.Max(usedWidth, x);
        }

        double height = y + rowHeight;
        return new Size(
            width > 0 ? width : usedWidth,
            height);
    }

    private static double FiniteWidth(double width) =>
        double.IsFinite(width) && width > 0 ? width : 0;

    private static void OnLayoutChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args) =>
        (sender as WrapPanel)?.InvalidateMeasure();
}
