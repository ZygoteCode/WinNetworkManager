using System.Windows.Controls;
using System.Windows.Input;
using WinNetworkManager.App.Common;
using WinNetworkManager.App.ViewModels;

namespace WinNetworkManager.App.Views;

/// <summary>Connection Explorer page.</summary>
public partial class ConnectionsView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ConnectionsView()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(FocusCommands.FocusSearch, (_, _) => SearchBox.Focus()));
    }

    private void OnTcpDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ConnectionsViewModel vm && vm.SelectedTcp is not null)
        {
            vm.GoToProcessCommand.Execute(null);
        }
    }
}
