using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Updatr.Core.Operations;

namespace Updatr.App.Views;

/// <summary>
/// A yes/no question. With items, they show as a grid (name, id, version now, version after, note)
/// above the note; without, the detail shows as text.
/// </summary>
public partial class ConfirmDialog : Window
{
    private static readonly IBrush Stripe = new SolidColorBrush(Color.FromArgb(0x14, 0x80, 0x80, 0x80));

    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public ConfirmDialog(Confirmation confirmation) : this()
    {
        TitleText.Text = confirmation.Title;

        if (confirmation.Items.Count == 0)
        {
            DetailText.Text = confirmation.Detail;
            return;
        }

        DetailScroll.IsVisible = false;
        ItemsBox.IsVisible = true;
        NoteText.Text = confirmation.Detail;
        NoteText.IsVisible = !string.IsNullOrWhiteSpace(confirmation.Detail);

        ConfirmationColumns c = confirmation.Columns;
        List<Column> columns =
        [
            new(c.Name, i => i.Name, 300),
            new(c.Id, i => i.Id, 280, Dim: true),
            new(c.Current, i => i.Current, null),
            new(c.Next, i => i.Next, null, Changed: true),
            new(c.Note, i => i.Note, null),
        ];
        columns.RemoveAll(col => col.Title != c.Name && confirmation.Items.All(i => string.IsNullOrEmpty(col.Get(i))));

        // Only the last column wraps; the rest size to their content (names and ids trim past a cap).
        columns[^1] = columns[^1] with { Wraps = true };
        HeaderRow.Child = Row(columns, col => Cell(col.Title, col, bold: true, dim: false));
        Rows.ItemsSource = confirmation.Items.Select((item, n) => new Border
        {
            Padding = new Thickness(10, 5),
            Background = n % 2 == 1 ? Stripe : Brushes.Transparent,
            Child = Row(columns, col => Cell(col.Get(item) ?? "", col,
                bold: col.Changed && col.Get(item) is { } next && next != item.Current,
                dim: col.Dim)),
        }).ToList();
    }

    /// <summary>Header and rows share column widths (shared size groups); the last column takes the rest.</summary>
    private static Grid Row(List<Column> columns, Func<Column, Control> cell)
    {
        Grid grid = new();

        for (int n = 0; n < columns.Count; n++)
        {
            bool last = n == columns.Count - 1;
            grid.ColumnDefinitions.Add(last
                ? new ColumnDefinition(GridLength.Star)
                : new ColumnDefinition(GridLength.Auto) { SharedSizeGroup = $"c{n}" });

            Control control = cell(columns[n]);
            control.Margin = new Thickness(0, 0, last ? 0 : 20, 0);
            Grid.SetColumn(control, n);
            grid.Children.Add(control);
        }

        return grid;
    }

    private static TextBlock Cell(string text, Column column, bool bold, bool dim)
    {
        TextBlock block = new()
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Opacity = dim ? 0.7 : 1,
        };

        if (column.Wraps)
        {
            block.TextWrapping = TextWrapping.Wrap;
        }
        else if (column.MaxWidth is { } max)
        {
            block.MaxWidth = max;
            block.TextTrimming = TextTrimming.CharacterEllipsis;
            ToolTip.SetTip(block, text.Length > 0 ? text : null);
        }

        return block;
    }

    private void OnYes(object? sender, RoutedEventArgs e) => Close(true);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(false);

    /// <param name="MaxWidth">Trim past this width (null: never trim).</param>
    /// <param name="Changed">Bold the value when it differs from the current one.</param>
    /// <param name="Dim">Secondary text (ids).</param>
    /// <param name="Wraps">The last column: takes the rest of the width and wraps.</param>
    private sealed record Column(string Title, Func<ConfirmationItem, string?> Get, double? MaxWidth, bool Changed = false, bool Dim = false, bool Wraps = false);
}
