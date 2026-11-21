using CommunityToolkit.Mvvm.ComponentModel;
using WinNetworkManager.Engine.Model;

namespace WinNetworkManager.App.ViewModels;

/// <summary>Navigation targets.</summary>
public enum PageKind
{
    /// <summary>Overview.</summary>
    Dashboard,

    /// <summary>Per-process traffic.</summary>
    Processes,

    /// <summary>TCP/UDP explorer.</summary>
    Connections,

    /// <summary>Adapters.</summary>
    Interfaces,

    /// <summary>Network tools.</summary>
    Diagnostics,

    /// <summary>Traffic history.</summary>
    History,

    /// <summary>Preferences.</summary>
    Settings,

    /// <summary>Process details (not in the navigation rail).</summary>
    Inspector,
}

/// <summary>Base class of page view models. Only the active page processes snapshots (cheap inactive pages).</summary>
public abstract class PageViewModel : ObservableObject
{
    private bool _isActive;

    /// <summary>Page title.</summary>
    public abstract string Title { get; }

    /// <summary>Page kind.</summary>
    public abstract PageKind Kind { get; }

    /// <summary>True while the page is displayed.</summary>
    public bool IsActive
    {
        get => _isActive;
        private set => SetProperty(ref _isActive, value);
    }

    /// <summary>Called when the page becomes visible, with the latest snapshot.</summary>
    public void Activate(EngineSnapshot snapshot)
    {
        IsActive = true;
        OnActivated();
        OnSnapshot(snapshot);
    }

    /// <summary>Called when another page becomes visible.</summary>
    public void Deactivate()
    {
        IsActive = false;
        OnDeactivated();
    }

    /// <summary>Delivers a snapshot (UI thread). Only called while active.</summary>
    public void Deliver(EngineSnapshot snapshot)
    {
        if (IsActive)
        {
            OnSnapshot(snapshot);
        }
    }

    /// <summary>Processes a snapshot.</summary>
    protected abstract void OnSnapshot(EngineSnapshot snapshot);

    /// <summary>Activation hook.</summary>
    protected virtual void OnActivated()
    {
    }

    /// <summary>Deactivation hook.</summary>
    protected virtual void OnDeactivated()
    {
    }
}
