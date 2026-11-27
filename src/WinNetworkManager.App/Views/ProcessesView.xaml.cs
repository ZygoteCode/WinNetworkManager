using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WinNetworkManager.App.Common;
using WinNetworkManager.App.ViewModels;

namespace WinNetworkManager.App.Views;

/// <summary>Processes page.</summary>
public partial class ProcessesView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ProcessesView()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(FocusCommands.FocusSearch, (_, _) => SearchBox.Focus()));
    }

    private ProcessesViewModel? ViewModel => DataContext as ProcessesViewModel;

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(Grid, source) is DataGridRow row)
        {
            ViewModel?.OpenDetailsCommand.Execute(row.Item as ProcessRowBase);
        }
    }

    private void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        var vm = ViewModel;
        if (vm?.SelectedRow is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.OpenDetailsCommand.Execute(vm.SelectedRow);
                e.Handled = true;
                break;
            case Key.Delete:
                vm.EndProcessCommand.Execute(null); // always asks for confirmation first
                e.Handled = true;
                break;
            case Key.Right when vm.SelectedRow is AppGroupRow { IsExpanded: false } group:
                vm.ToggleGroupCommand.Execute(group);
                e.Handled = true;
                break;
            case Key.Left when vm.SelectedRow is AppGroupRow { IsExpanded: true } group:
                vm.ToggleGroupCommand.Execute(group);
                e.Handled = true;
                break;
        }
    }
}
