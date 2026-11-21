using Microsoft.Extensions.DependencyInjection;
using WinNetworkManager.App.Shell;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Registers page view models (one instance per page, kept for the whole session so state survives navigation).</summary>
internal static class ViewModelRegistration
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<Func<ShellViewModel>>(sp => () => sp.GetRequiredService<ShellViewModel>());
        Page<DashboardViewModel>(services);
        Page<ProcessesViewModel>(services);
        Page<ConnectionsViewModel>(services);
        Page<InterfacesViewModel>(services);
        Page<DiagnosticsViewModel>(services);
        Page<HistoryViewModel>(services);
        Page<SettingsViewModel>(services);
    }

    private static void Page<T>(IServiceCollection services)
        where T : PageViewModel
    {
        services.AddSingleton<T>();
        services.AddSingleton<PageViewModel>(sp => sp.GetRequiredService<T>());
    }
}
