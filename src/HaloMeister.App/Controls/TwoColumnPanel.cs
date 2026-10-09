using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HaloMeister.App.Controls;

/// <summary>
/// Lays children out two per row, both columns the same width.
/// </summary>
public sealed class TwoColumnPanel : Panel
{
    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(TwoColumnPanel),
            new PropertyMetadata(12.0, OnLayoutChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(TwoColumnPanel),
            new PropertyMetadata(12.0, OnLayoutChanged));

    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    private double _arrangedWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = FiniteWidth(availableSize.Width);
        if (width <= 0)
            width = FiniteWidth(_arrangedWidth);
        if (width <= 0)
            return new Size(0, 0);

        return new Size(width, MeasureRows(width));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = FiniteWidth(finalSize.Width);
        if (width <= 0)
            width = FiniteWidth(_arrangedWidth);
        if (width <= 0)
            return finalSize;

        if (Math.Abs(width - _arrangedWidth) > 0.5)
            _arrangedWidth = width;

        double columnWidth = ColumnWidth(width);
        double y = 0;
        for (int index = 0; index < Children.Count; index += 2)
        {
            UIElement left = Children[index];
            UIElement? right = index + 1 < Children.Count ? Children[index + 1] : null;
            Stretch(left);
            left.Measure(new Size(columnWidth, double.PositiveInfinity));
            double rowHeight = left.DesiredSize.Height;
            if (right is not null)
            {
                Stretch(right);
                right.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, right.DesiredSize.Height);
            }

            left.Arrange(new Rect(0, y, columnWidth, rowHeight));
            right?.Arrange(new Rect(columnWidth + ColumnSpacing, y, columnWidth, rowHeight));
            y += rowHeight;
            if (index + 2 < Children.Count)
                y += RowSpacing;
        }

        return new Size(width, y);
    }

    private double MeasureRows(double width)
    {
        double columnWidth = ColumnWidth(width);
        double height = 0;
        for (int index = 0; index < Children.Count; index += 2)
        {
            UIElement left = Children[index];
            UIElement? right = index + 1 < Children.Count ? Children[index + 1] : null;
            Stretch(left);
            left.Measure(new Size(columnWidth, double.PositiveInfinity));
            double rowHeight = left.DesiredSize.Height;
            if (right is not null)
            {
                Stretch(right);
                right.Measure(new Size(columnWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, right.DesiredSize.Height);
            }

            height += rowHeight;
            if (index + 2 < Children.Count)
                height += RowSpacing;
        }

        return height;
    }

    private double ColumnWidth(double width) =>
        Math.Max(0, (width - ColumnSpacing) / 2);

    private static double FiniteWidth(double width) =>
        double.IsFinite(width) && width > 0 ? width : 0;

    private static void Stretch(UIElement child)
    {
        if (child is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        (sender as TwoColumnPanel)?.InvalidateMeasure();
}
