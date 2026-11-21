using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using WinNetworkManager.App.Services;
using WinNetworkManager.Core.Processes;
using WinNetworkManager.Core.Traffic;
using WinNetworkManager.Engine.Model;

namespace WinNetworkManager.App.ViewModels;

/// <summary>
/// Common row of the process table (a process instance or an application group). Numeric properties drive sorting
/// and exports; text properties are formatted only when the number changes.
/// </summary>
public abstract partial class ProcessRowBase : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string PidText { get; set; } = string.Empty;
    [ObservableProperty] public partial string Publisher { get; set; } = string.Empty;
    [ObservableProperty] public partial string Description { get; set; } = string.Empty;
    [ObservableProperty] public partial string PathText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UserText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial string StartedText { get; set; } = string.Empty;
    [ObservableProperty] public partial string MonitoredText { get; set; } = string.Empty;
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial bool IsExited { get; set; }
    [ObservableProperty] public partial bool IsActive { get; set; }
    [ObservableProperty] public partial bool IsSystem { get; set; }
    [ObservableProperty] public partial IReadOnlyList<float> Trend { get; set; } = [];

    [ObservableProperty] public partial string DownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TcpDownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TcpUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UdpDownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string UdpUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TotalDownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TotalUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TotalText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AvgDownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string AvgUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PeakDownText { get; set; } = string.Empty;
    [ObservableProperty] public partial string PeakUpText { get; set; } = string.Empty;
    [ObservableProperty] public partial string ConnectionsText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RemotesText { get; set; } = string.Empty;
    [ObservableProperty] public partial string RetransText { get; set; } = string.Empty;
    [ObservableProperty] public partial string TotalsToolTip { get; set; } = string.Empty;

    /// <summary>Nesting level (0 = top, 1 = child of a group).</summary>
    public int Level { get; protected set; }

    /// <summary>True for group rows.</summary>
    public virtual bool IsGroup => false;

    /// <summary>Indentation of the name cell.</summary>
    public double Indent => Level * 22;

    // Numeric values (sorting, export).
    public int Pid { get; protected set; }
    public double Down { get; protected set; }
    public double Up { get; protected set; }
    public double TcpDown { get; protected set; }
    public double TcpUp { get; protected set; }
    public double UdpDown { get; protected set; }
    public double UdpUp { get; protected set; }
    public long TotalDown { get; protected set; }
    public long TotalUp { get; protected set; }
    public long Total => TotalDown + TotalUp;
    public double AvgDown { get; protected set; }
    public double AvgUp { get; protected set; }
    public double PeakDown { get; protected set; }
    public double PeakUp { get; protected set; }
    public int Connections { get; protected set; }
    public int Remotes { get; protected set; }
    public long Retransmitted { get; protected set; }
    public DateTimeOffset? Started { get; protected set; }
    public TimeSpan Monitored { get; protected set; }
    public bool TrafficAvailable { get; protected set; }
    public double Rate => Down + Up;

    /// <summary>Accessible row summary.</summary>
    public string AccessibleName => $"{Name}, PID {PidText}, download {DownText}, upload {UpText}, {StatusText}";

    /// <summary>Re-formats every text (unit change).</summary>
    public void Reformat() => Format(force: true);

    /// <summary>Formats texts from the numeric values.</summary>
    protected void Format(bool force)
    {
        bool a = TrafficAvailable;
        DownText = Fmt.Rate(Down, a);
        UpText = Fmt.Rate(Up, a);
        TcpDownText = Fmt.Rate(TcpDown, a);
        TcpUpText = Fmt.Rate(TcpUp, a);
        UdpDownText = Fmt.Rate(UdpDown, a);
        UdpUpText = Fmt.Rate(UdpUp, a);
        bool totals = a || Total > 0;
        TotalDownText = Fmt.Bytes(TotalDown, totals);
        TotalUpText = Fmt.Bytes(TotalUp, totals);
        TotalText = Fmt.Bytes(Total, totals);
        AvgDownText = Fmt.Rate(AvgDown, totals);
        AvgUpText = Fmt.Rate(AvgUp, totals);
        PeakDownText = Fmt.Rate(PeakDown, totals);
        PeakUpText = Fmt.Rate(PeakUp, totals);
        RetransText = totals ? Fmt.Bytes(Retransmitted) : Fmt.NotAvailable;
        ConnectionsText = Connections.ToString(System.Globalization.CultureInfo.CurrentCulture);
        RemotesText = Remotes.ToString(System.Globalization.CultureInfo.CurrentCulture);
        MonitoredText = Monitored > TimeSpan.Zero ? Fmt.Duration(Monitored) : Fmt.NotAvailable;
        TotalsToolTip = totals
            ? $"Downloaded {Fmt.Exact(TotalDown)}\nUploaded {Fmt.Exact(TotalUp)}\nSince monitoring started; bytes before the collector started are not known."
            : "Per-process traffic needs the collector (administrator rights).";
        _ = force;
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>A process instance row.</summary>
public sealed partial class ProcessRow : ProcessRowBase
{
    /// <summary>Creates a row.</summary>
    public ProcessRow(ProcessKey key)
    {
        Key = key;
        Pid = key.Pid;
        PidText = key.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Instance key.</summary>
    public ProcessKey Key { get; }

    /// <summary>Latest view.</summary>
    public ProcessView? View { get; private set; }

    /// <summary>Application id (grouping).</summary>
    public string AppId { get; private set; } = string.Empty;

    /// <summary>Full path if known.</summary>
    public string? Path { get; private set; }

    /// <summary>Updates from a snapshot view.</summary>
    public void Update(ProcessView view, int level)
    {
        bool changed = View is null || !ReferenceEquals(View, view);
        View = view;
        Level = level;
        AppId = view.AppId;
        if (!changed)
        {
            return;
        }

        Name = view.Name;
        Path = view.Path.HasValue ? view.Path.Value : null;
        PathText = view.Path.HasValue ? view.Path.Value! : Probe<string>.Explain(view.Path.Status);
        Description = view.Description ?? (view.Services.Count > 0 ? string.Join(", ", view.Services) : string.Empty);
        Publisher = view.Signature?.Publisher ?? view.Company ?? (view.Signature is { } s ? s.Description : string.Empty);
        UserText = view.UserName ?? string.Empty;
        IsSystem = view.IsSystem;
        IsExited = view.State == ProcessState.Exited;
        StatusText = view.State == ProcessState.Exited
            ? (view.ExitCode is { } code ? $"Exited ({code})" : "Exited")
            : view.Identity == IdentityConfidence.PidAndName ? "Running (limited access)" : "Running";
        Started = view.StartTime;
        StartedText = Fmt.DateTime(view.StartTime);
        TrafficAvailable = view.Traffic is TrafficAvailability.Available or TrafficAvailability.Partial;
        Down = view.Rates.Received;
        Up = view.Rates.Sent;
        TcpDown = view.Rates.TcpReceived;
        TcpUp = view.Rates.TcpSent;
        UdpDown = view.Rates.UdpReceived;
        UdpUp = view.Rates.UdpSent;
        TotalDown = view.Totals.Received;
        TotalUp = view.Totals.Sent;
        AvgDown = view.AverageReceiveRate;
        AvgUp = view.AverageSendRate;
        PeakDown = view.PeakReceiveRate;
        PeakUp = view.PeakSendRate;
        Retransmitted = view.RetransmittedBytes;
        Connections = view.TcpConnections + view.UdpEndpoints;
        Remotes = view.RemoteAddresses;
        Monitored = view.MonitoredDuration;
        IsActive = view.Rates.Total > 0;
        Trend = view.Sparkline;
        Format(force: false);
    }
}

/// <summary>An application group row (several instances of the same executable).</summary>
public sealed partial class AppGroupRow : ProcessRowBase
{
    /// <summary>Creates a group.</summary>
    public AppGroupRow(string appId)
    {
        AppId = appId;
    }

    /// <summary>Application id.</summary>
    public string AppId { get; }

    /// <summary>Expanded state.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Instances in the group.</summary>
    public List<ProcessRow> Children { get; } = [];

    /// <inheritdoc />
    public override bool IsGroup => true;

    /// <summary>Path of the executable (for the icon).</summary>
    public string? Path => Children.Select(c => c.Path).FirstOrDefault(p => p is not null);

    /// <summary>Aggregates the children.</summary>
    public void Aggregate()
    {
        var first = Children[0];
        Level = 0;
        Name = first.View?.Description is { Length: > 0 } d ? d : first.Name;
        PidText = $"{Children.Count} processes";
        Pid = Children.Min(c => c.Pid);
        Description = first.Name;
        Publisher = first.Publisher;
        PathText = first.PathText;
        UserText = string.Join(", ", Children.Select(c => c.UserText).Where(u => u.Length > 0).Distinct());
        IsSystem = Children.All(c => c.IsSystem);
        IsExited = Children.All(c => c.IsExited);
        StatusText = IsExited ? "Exited" : $"{Children.Count(c => !c.IsExited)} running";
        TrafficAvailable = Children.Any(c => c.TrafficAvailable);
        Down = Children.Sum(c => c.Down);
        Up = Children.Sum(c => c.Up);
        TcpDown = Children.Sum(c => c.TcpDown);
        TcpUp = Children.Sum(c => c.TcpUp);
        UdpDown = Children.Sum(c => c.UdpDown);
        UdpUp = Children.Sum(c => c.UdpUp);
        TotalDown = Children.Sum(c => c.TotalDown);
        TotalUp = Children.Sum(c => c.TotalUp);
        AvgDown = Children.Sum(c => c.AvgDown);
        AvgUp = Children.Sum(c => c.AvgUp);
        PeakDown = Children.Max(c => c.PeakDown);
        PeakUp = Children.Max(c => c.PeakUp);
        Retransmitted = Children.Sum(c => c.Retransmitted);
        Connections = Children.Sum(c => c.Connections);
        Remotes = Children.Sum(c => c.Remotes);
        Started = Children.Min(c => c.Started);
        StartedText = Fmt.DateTime(Started);
        Monitored = Children.Max(c => c.Monitored);
        IsActive = Children.Any(c => c.IsActive);
        var spark = new float[Children.Max(c => c.Trend.Count)];
        foreach (var child in Children)
        {
            for (int i = 0; i < child.Trend.Count; i++)
            {
                float v = child.Trend[i];
                spark[i] = float.IsNaN(v) ? spark[i] : (float.IsNaN(spark[i]) ? v : spark[i] + v);
            }
        }

        Trend = spark;
        Format(force: false);
    }
}
