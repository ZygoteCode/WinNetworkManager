using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinNetworkManager.App.Services;

/// <summary>
/// Loads small executable icons on a dedicated STA thread (shell APIs may block or need COM) and caches them as
/// frozen bitmaps. Callers get a placeholder (null) until the icon is ready and are notified via callback.
/// </summary>
public sealed partial class IconService : IDisposable
{
    private int _disposed;

    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly BlockingCollection<(string Path, Action<ImageSource?> Callback)> _queue = new(boundedCapacity: 2048);
    private readonly Thread _thread;
    private readonly Dispatcher _dispatcher;

    /// <summary>Creates the service.</summary>
    public IconService(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "Icon loader" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Returns a cached icon or schedules loading; <paramref name="onLoaded"/> runs on the UI thread.</summary>
    public ImageSource? Get(string? path, Action<ImageSource?> onLoaded)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_cache.TryGetValue(path, out var cached))
        {
            return cached;
        }

        if (Volatile.Read(ref _disposed) == 1)
        {
            return null; // shutting down
        }

        _cache.TryAdd(path, null);
        try
        {
            _queue.TryAdd((path, onLoaded));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // Disposed concurrently during shutdown.
        }

        return null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(1));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var (path, callback) in _queue.GetConsumingEnumerable())
        {
            ImageSource? image = Load(path);
            _cache[path] = image;
            if (image is not null)
            {
                _dispatcher.BeginInvoke(DispatcherPriority.Background, () => callback(image));
            }
        }
    }

    private static ImageSource? Load(string path)
    {
        var info = default(SHFILEINFOW);
        nint result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFOW>(), SHGFI_ICON | SHGFI_SMALLICON);
        if (result == 0 || info.hIcon == 0)
        {
            return null;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_SMALLICON = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHGetFileInfoW")]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref SHFILEINFOW info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);
}
