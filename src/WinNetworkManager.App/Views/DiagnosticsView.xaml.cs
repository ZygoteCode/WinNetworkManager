using System.Windows;
using System.Windows.Controls;
using WinNetworkManager.App.ViewModels;

namespace WinNetworkManager.App.Views;

/// <summary>Diagnostics page.</summary>
public partial class DiagnosticsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public DiagnosticsView() => InitializeComponent();

    private void OnHashDragOver(object sender, DragEventArgs e)
    {
        e.Effects = SingleFile(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnHashDrop(object sender, DragEventArgs e)
    {
        if (SingleFile(e) is { } path && DataContext is DiagnosticsViewModel vm)
        {
            vm.HashDroppedFile(path);
            e.Handled = true;
        }
    }

    private static string? SingleFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && System.IO.File.Exists(files[0]) ? files[0] : null;
}
