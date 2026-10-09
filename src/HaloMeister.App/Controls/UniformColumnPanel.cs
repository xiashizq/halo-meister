using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace HaloMeister.App.Controls;

/// <summary>
/// Equal-width columns. Uses as many as fit, up to <see cref="MaxColumns"/>.
/// Children may span several columns via <see cref="ColumnSpanProperty"/>.
/// </summary>
public sealed class UniformColumnPanel : Panel
{
    public static readonly DependencyProperty MaxColumnsProperty =
        DependencyProperty.Register(
            nameof(MaxColumns),
            typeof(int),
            typeof(UniformColumnPanel),
            new PropertyMetadata(4, OnLayoutChanged));

    public static readonly DependencyProperty MinColumnWidthProperty =
        DependencyProperty.Register(
            nameof(MinColumnWidth),
            typeof(double),
            typeof(UniformColumnPanel),
            new PropertyMetadata(280.0, OnLayoutChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(
            nameof(ColumnSpacing),
            typeof(double),
            typeof(UniformColumnPanel),
            new PropertyMetadata(16.0, OnLayoutChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(
            nameof(RowSpacing),
            typeof(double),
            typeof(UniformColumnPanel),
            new PropertyMetadata(0.0, OnLayoutChanged));

    public static readonly DependencyProperty ColumnSpanProperty =
        DependencyProperty.RegisterAttached(
            "ColumnSpan",
            typeof(int),
            typeof(UniformColumnPanel),
            new PropertyMetadata(1, OnChildSpanChanged));

    public static int GetColumnSpan(DependencyObject element) =>
        (int)element.GetValue(ColumnSpanProperty);

    public static void SetColumnSpan(DependencyObject element, int value) =>
        element.SetValue(ColumnSpanProperty, value);

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

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

    private readonly record struct Cell(UIElement Child, int Row, int Column, int Span);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = ResolveWidth(availableSize.Width);
        if (width <= 0)
            return new Size(0, 0);

        Layout(width, arrange: false, out double height);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = ResolveWidth(finalSize.Width);
        if (width <= 0)
            return finalSize;

        if (Math.Abs(width - _arrangedWidth) > 0.5)
            _arrangedWidth = width;

        Layout(width, arrange: true, out double height);
        return new Size(width, height);
    }

    private void Layout(double width, bool arrange, out double height)
    {
        int columns = ColumnCount(width);
        double columnWidth = ColumnWidth(width, columns);

        var cells = new List<Cell>();
        int row = 0;
        int column = 0;
        foreach (UIElement child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;

            int span = Math.Clamp(GetColumnSpan(child), 1, columns);
            if (column + span > columns)
            {
                row++;
                column = 0;
            }

            cells.Add(new Cell(child, row, column, span));
            column += span;
            if (column >= columns)
            {
                row++;
                column = 0;
            }
        }

        int rowCount = cells.Count == 0 ? 0 : cells.Max(cell => cell.Row) + 1;
        var rowHeights = new double[rowCount];
        foreach (Cell cell in cells)
        {
            Stretch(cell.Child);
            double cellWidth = SpanWidth(columnWidth, cell.Span);
            cell.Child.Measure(new Size(cellWidth, double.PositiveInfinity));
            rowHeights[cell.Row] = Math.Max(rowHeights[cell.Row], cell.Child.DesiredSize.Height);
        }

        var rowTops = new double[rowCount];
        double y = 0;
        for (int index = 0; index < rowCount; index++)
        {
            rowTops[index] = y;
            y += rowHeights[index];
            if (index < rowCount - 1)
                y += RowSpacing;
        }

        height = y;
        if (!arrange)
            return;

        foreach (Cell cell in cells)
        {
            double x = cell.Column * (columnWidth + ColumnSpacing);
            cell.Child.Arrange(new Rect(
                x,
                rowTops[cell.Row],
                SpanWidth(columnWidth, cell.Span),
                rowHeights[cell.Row]));
        }
    }

    private double SpanWidth(double columnWidth, int span) =>
        columnWidth * span + ColumnSpacing * (span - 1);

    private int ColumnCount(double width)
    {
        int max = Math.Max(1, MaxColumns);
        for (int columns = max; columns > 1; columns--)
        {
            if (ColumnWidth(width, columns) >= MinColumnWidth)
                return columns;
        }

        return 1;
    }

    private double ColumnWidth(double width, int columns)
    {
        double spacing = ColumnSpacing * Math.Max(0, columns - 1);
        return Math.Max(0, (width - spacing) / columns);
    }

    private double ResolveWidth(double width)
    {
        if (double.IsFinite(width) && width > 0)
            return width;
        return double.IsFinite(_arrangedWidth) && _arrangedWidth > 0 ? _arrangedWidth : 0;
    }

    private static void Stretch(UIElement child)
    {
        if (child is FrameworkElement element)
        {
            element.HorizontalAlignment = HorizontalAlignment.Stretch;
            element.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    private static void OnChildSpanChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement { Parent: UniformColumnPanel panel })
            panel.InvalidateMeasure();
    }

    private static void OnLayoutChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        (sender as UniformColumnPanel)?.InvalidateMeasure();
}
