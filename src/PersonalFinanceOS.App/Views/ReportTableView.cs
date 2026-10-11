using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PersonalFinanceOS.Application.Reports;

namespace PersonalFinanceOS.App.Views;

/// <summary>
/// One report table on screen. The columns differ per table, so the grid is built here instead of in XAML. Totals are
/// bold with a line above; estimates and context lines are grey, as in the PDF.
/// </summary>
public sealed class ReportTableView : UserControl
{
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Content = DataContext is FormattedTable table ? Build(table) : null;
    }

    private static Control Build(FormattedTable table)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = table.Title, Classes = { "title" }, TextWrapping = TextWrapping.Wrap });
        if (table.Rows.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = table.EmptyText, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            var grid = new Grid { ColumnSpacing = 14 };
            var firstText = true;
            foreach (var column in table.Columns)
            {
                grid.ColumnDefinitions.Add(column.Align == ColumnAlign.Right
                    ? new ColumnDefinition(GridLength.Auto)
                    : new ColumnDefinition(firstText ? 2 : 1, GridUnitType.Star) { MinWidth = 60 });
                firstText &= column.Align == ColumnAlign.Right;
            }

            var row = 0;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var i = 0; i < table.Columns.Count; i++)
            {
                Add(grid, Cell(table.Columns[i].Header, table.Columns[i].Align, "caption", FontWeight.SemiBold), row, i);
            }

            row++;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Add(grid, new Border { Classes = { "separator" }, Margin = new Thickness(0, 4) }, row, 0, table.Columns.Count);
            foreach (var r in table.Rows)
            {
                row++;
                if (r.Style == RowStyle.Total)
                {
                    grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    Add(grid, new Border { Classes = { "separator" }, Margin = new Thickness(0, 2) }, row, 0, table.Columns.Count);
                    row++;
                }

                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var i = 0; i < table.Columns.Count && i < r.Cells.Count; i++)
                {
                    var cell = Cell(
                        r.Cells[i],
                        table.Columns[i].Align,
                        r.Style == RowStyle.Subtle ? "secondary" : null,
                        r.Style == RowStyle.Total ? FontWeight.SemiBold : FontWeight.Normal);
                    cell.Margin = new Thickness(0, 3);
                    Add(grid, cell, row, i);
                }
            }

            panel.Children.Add(grid);
        }

        if (table.Note is { } note)
        {
            panel.Children.Add(new TextBlock { Text = "ℹ  " + note, Classes = { "caption" }, TextWrapping = TextWrapping.Wrap });
        }

        return panel;
    }

    private static TextBlock Cell(string text, ColumnAlign align, string? style, FontWeight weight)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = weight,
            TextWrapping = align == ColumnAlign.Left ? TextWrapping.Wrap : TextWrapping.NoWrap,
            HorizontalAlignment = align == ColumnAlign.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            TextAlignment = align == ColumnAlign.Right ? TextAlignment.Right : TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (style is not null)
        {
            block.Classes.Add(style);
        }

        return block;
    }

    private static void Add(Grid grid, Control control, int row, int column, int span = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, span);
        grid.Children.Add(control);
    }
}
