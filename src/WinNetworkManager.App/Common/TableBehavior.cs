using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Settings;

namespace WinNetworkManager.App.Common;

/// <summary>Implemented by view models that sort their own rows (the DataGrid never sorts by itself).</summary>
public interface ISortableTable
{
    /// <summary>Current sort column id.</summary>
    string SortColumn { get; }

    /// <summary>Current direction.</summary>
    bool SortDescending { get; }

    /// <summary>Sorts by a column (direction toggles when it is already the sort column).</summary>
    void SortBy(string column, bool? descending = null);
}

/// <summary>
/// Attached behaviour shared by all data tables: routes header clicks to the view model's sort, shows sort glyphs,
/// offers a header context menu to show/hide columns, and persists column order, width and visibility per table id.
/// Column ids are taken from <see cref="DataGridColumn.SortMemberPath"/>.
/// </summary>
public static class TableBehavior
{
    /// <summary>Table id used as the settings key; setting it enables the behaviour.</summary>
    public static readonly DependencyProperty TableIdProperty =
        DependencyProperty.RegisterAttached("TableId", typeof(string), typeof(TableBehavior), new PropertyMetadata(null, OnTableIdChanged));

    private static readonly DependencyProperty SaveTimerProperty =
        DependencyProperty.RegisterAttached("SaveTimer", typeof(DispatcherTimer), typeof(TableBehavior));

    /// <summary>Gets the table id.</summary>
    public static string? GetTableId(DependencyObject element) => (string?)element.GetValue(TableIdProperty);

    /// <summary>Sets the table id.</summary>
    public static void SetTableId(DependencyObject element, string? value) => element.SetValue(TableIdProperty, value);

    private static void OnTableIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DataGrid grid || e.NewValue is not string)
        {
            return;
        }

        grid.Sorting += OnSorting;
        grid.Loaded += (_, _) =>
        {
            ApplyLayout(grid);
            UpdateSortGlyphs(grid);
            BuildHeaderMenu(grid);
        };
        grid.ColumnReordered += (_, _) => ScheduleSave(grid);
        grid.DataContextChanged += (_, _) => UpdateSortGlyphs(grid);
        grid.PreviewMouseLeftButtonUp += (_, _) => ScheduleSave(grid); // after column resize drags
        grid.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+C copies the selected rows as tab-separated text (header included) — built into DataGrid; keep it.
        if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
        {
            var grid = (DataGrid)sender;
            if (grid.ContextMenu is { } menu)
            {
                menu.PlacementTarget = grid;
                menu.IsOpen = true;
                e.Handled = true;
            }
        }
    }

    private static void OnSorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var grid = (DataGrid)sender;
        if (grid.DataContext is not ISortableTable table || string.IsNullOrEmpty(e.Column.SortMemberPath))
        {
            return;
        }

        table.SortBy(e.Column.SortMemberPath);
        UpdateSortGlyphs(grid);
        ScheduleSave(grid);
    }

    private static void UpdateSortGlyphs(DataGrid grid)
    {
        if (grid.DataContext is not ISortableTable table)
        {
            return;
        }

        foreach (var column in grid.Columns)
        {
            column.SortDirection = column.SortMemberPath == table.SortColumn
                ? (table.SortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                : null;
        }
    }

    private static void BuildHeaderMenu(DataGrid grid)
    {
        var menu = new ContextMenu();
        foreach (var column in grid.Columns.Where(c => !string.IsNullOrEmpty(c.SortMemberPath) || c.Header is string))
        {
            string header = column.Header as string ?? column.SortMemberPath;
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = column.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            if (Equals(column.Header, grid.Columns[0].Header))
            {
                item.IsEnabled = false; // the identity column cannot be hidden
            }

            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                ScheduleSave(grid);
            };
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var reset = new MenuItem { Header = "Reset columns" };
        reset.Click += (_, _) =>
        {
            foreach (var column in grid.Columns)
            {
                column.Visibility = Visibility.Visible;
                column.Width = DataGridLength.Auto;
            }

            ScheduleSave(grid, clear: true);
            BuildHeaderMenu(grid);
        };
        menu.Items.Add(reset);

        var headerStyle = new Style(typeof(DataGridColumnHeader), grid.ColumnHeaderStyle ?? (Style?)grid.TryFindResource(typeof(DataGridColumnHeader)));
        headerStyle.Setters.Add(new Setter(FrameworkElement.ContextMenuProperty, menu));
        grid.ColumnHeaderStyle = headerStyle;
    }

    private static void ApplyLayout(DataGrid grid)
    {
        string id = GetTableId(grid)!;
        var settings = Settings()?.Current;
        if (settings is null || !settings.Tables.TryGetValue(id, out var layout))
        {
            return;
        }

        var byId = grid.Columns.Where(c => !string.IsNullOrEmpty(c.SortMemberPath)).ToDictionary(c => c.SortMemberPath);
        int index = 0;
        foreach (var saved in layout.Columns)
        {
            if (!byId.TryGetValue(saved.Id, out var column))
            {
                continue;
            }

            // The identity column (first in XAML) can never be hidden.
            column.Visibility = saved.Visible || ReferenceEquals(column, grid.Columns[0]) ? Visibility.Visible : Visibility.Collapsed;

            if (saved.Width is { } width)
            {
                column.Width = new DataGridLength(width);
            }

            if (index < grid.Columns.Count)
            {
                column.DisplayIndex = Math.Min(index, grid.Columns.Count - 1);
            }

            index++;
        }

        if (grid.DataContext is ISortableTable table && !string.IsNullOrEmpty(layout.SortColumn) && byId.ContainsKey(layout.SortColumn))
        {
            table.SortBy(layout.SortColumn, layout.SortDescending);
        }
    }

    private static void ScheduleSave(DataGrid grid, bool clear = false)
    {
        if (grid.GetValue(SaveTimerProperty) is not DispatcherTimer timer)
        {
            timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Save(grid);
            };
            grid.SetValue(SaveTimerProperty, timer);
        }

        if (clear)
        {
            Settings()?.Update(s =>
            {
                var tables = new Dictionary<string, TableLayout>(s.Tables);
                tables.Remove(GetTableId(grid)!);
                return s with { Tables = tables };
            });
            return;
        }

        timer.Stop();
        timer.Start();
    }

    private static void Save(DataGrid grid)
    {
        var service = Settings();
        if (service is null)
        {
            return;
        }

        string id = GetTableId(grid)!;
        var columns = grid.Columns
            .Where(c => !string.IsNullOrEmpty(c.SortMemberPath))
            .OrderBy(c => c.DisplayIndex)
            .Select(c => new ColumnLayout(c.SortMemberPath, c.Visibility == Visibility.Visible, c.Width.IsAbsolute ? c.Width.Value : (c.ActualWidth > 0 ? Math.Round(c.ActualWidth) : null)))
            .ToArray();
        var table = grid.DataContext as ISortableTable;
        try
        {
            service.Update(s =>
            {
                var tables = new Dictionary<string, TableLayout>(s.Tables)
                {
                    [id] = new TableLayout { Columns = columns, SortColumn = table?.SortColumn, SortDescending = table?.SortDescending ?? true },
                };
                return s with { Tables = tables };
            });
        }
        catch (IOException)
        {
            // Layout persistence is best effort.
        }
    }

    private static SettingsService? Settings() =>
        Application.Current is App ? App.Services.GetService<SettingsService>() : null;
}
