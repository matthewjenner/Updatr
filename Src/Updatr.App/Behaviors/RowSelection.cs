using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Updatr.App.Behaviors;

/// <summary>A list row the user can pick by clicking it (no checkboxes).</summary>
public interface ISelectableRow : INotifyPropertyChanged
{
    bool IsSelected { get; set; }

    /// <summary>False for rows that cannot be picked here (shown, but clicks do nothing).</summary>
    bool CanSelect => true;
}

/// <summary>
/// Whole-row picking. On a DataGrid (<c>RowSelection.Enabled="True"</c>): a click toggles the row,
/// Space toggles the current row, and the grid's own selection highlight always mirrors each row's
/// <see cref="ISelectableRow.IsSelected"/> - the ViewModel is the truth, so filtering, sorting, and
/// refreshing never lose picks. On any other control (<c>RowSelection.ToggleOnClick="True"</c>,
/// typically a row's Border): a click toggles its DataContext; style the highlight with
/// <c>Classes.selected="{Binding IsSelected}"</c>.
/// </summary>
public static class RowSelection
{
    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("Enabled", typeof(RowSelection));

    public static readonly AttachedProperty<bool> ToggleOnClickProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("ToggleOnClick", typeof(RowSelection));

    static RowSelection()
    {
        EnabledProperty.Changed.AddClassHandler<DataGrid>((grid, e) =>
        {
            if (e.NewValue is true)
                _ = new GridSync(grid);
        });

        ToggleOnClickProperty.Changed.AddClassHandler<InputElement>((element, e) =>
        {
            if (e.NewValue is true)
                element.AddHandler(InputElement.PointerReleasedEvent, OnElementReleased, RoutingStrategies.Bubble);
        });
    }

    public static bool GetEnabled(DataGrid grid) => grid.GetValue(EnabledProperty);

    public static void SetEnabled(DataGrid grid, bool value) => grid.SetValue(EnabledProperty, value);

    public static bool GetToggleOnClick(InputElement element) => element.GetValue(ToggleOnClickProperty);

    public static void SetToggleOnClick(InputElement element, bool value) => element.SetValue(ToggleOnClickProperty, value);

    private static void OnElementReleased(object? sender, PointerReleasedEventArgs e)
    {
        // Buttons inside the row (Keep, Revert) handle their own clicks.
        if (e.Handled || e.InitialPressMouseButton != MouseButton.Left || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null)
            return;

        if (sender is StyledElement { DataContext: ISelectableRow { CanSelect: true } row })
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
        }
    }

    /// <summary>Keeps one DataGrid's selection equal to its rows' IsSelected.</summary>
    private sealed class GridSync
    {
        private readonly DataGrid _grid;
        private readonly HashSet<ISelectableRow> _watched = [];
        private INotifyCollectionChanged? _source;
        private bool _applying;
        private bool _queued;

        public GridSync(DataGrid grid)
        {
            _grid = grid;
            grid.SelectionMode = DataGridSelectionMode.Extended;
            grid.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel);
            grid.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            grid.SelectionChanged += (_, _) => Queue();
            grid.PropertyChanged += (_, e) =>
            {
                if (e.Property == ItemsControl.ItemsSourceProperty || e.Property == DataGrid.ItemsSourceProperty)
                    Attach();
            };
            Attach();
        }

        private void OnPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(_grid).Properties.IsLeftButtonPressed)
                return;

            // Header clicks (sorting) and anything outside a row keep their normal behavior.
            if ((e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true) is not { DataContext: ISelectableRow row })
                return;

            if (row.CanSelect)
                row.IsSelected = !row.IsSelected;

            e.Handled = true;
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && (e.Source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext is ISelectableRow { CanSelect: true } row)
            {
                row.IsSelected = !row.IsSelected;
                e.Handled = true;
            }
        }

        private void Attach()
        {
            if (_source is not null)
                _source.CollectionChanged -= OnCollectionChanged;

            _source = _grid.ItemsSource as INotifyCollectionChanged;

            if (_source is not null)
                _source.CollectionChanged += OnCollectionChanged;

            WatchAll();
            Queue();
        }

        private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                foreach (ISelectableRow row in e.NewItems.OfType<ISelectableRow>())
                    Watch(row);
            }
            else
            {
                WatchAll();
            }

            Queue();
        }

        private void WatchAll()
        {
            if (_grid.ItemsSource is IEnumerable items)
            {
                foreach (ISelectableRow row in items.OfType<ISelectableRow>())
                    Watch(row);
            }
        }

        private void Watch(ISelectableRow row)
        {
            if (_watched.Add(row))
                row.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ISelectableRow.IsSelected))
                        Queue();
                };
        }

        /// <summary>One reconcile per burst of changes, after the grid has finished its own work.</summary>
        private void Queue()
        {
            if (_applying || _queued)
                return;

            _queued = true;
            Dispatcher.UIThread.Post(Apply, DispatcherPriority.Background);
        }

        private void Apply()
        {
            _queued = false;

            if (_grid.ItemsSource is not IEnumerable items)
                return;

            _applying = true;

            try
            {
                _grid.SelectedItems.Clear();

                foreach (ISelectableRow row in items.OfType<ISelectableRow>().Where(r => r.IsSelected))
                    _grid.SelectedItems.Add(row);
            }
            finally
            {
                _applying = false;
            }
        }
    }
}
