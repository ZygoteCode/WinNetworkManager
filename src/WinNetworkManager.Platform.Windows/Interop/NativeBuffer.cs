using System.Runtime.InteropServices;

namespace WinNetworkManager.Platform.Windows.Interop;

/// <summary>
/// Reusable, growable unmanaged buffer for "size query → fill" Win32 APIs. Reusing it across polls keeps the
/// steady state allocation-free. Not thread-safe: one instance per reader.
/// </summary>
internal sealed unsafe class NativeBuffer : IDisposable
{
    private byte* _pointer;

    public NativeBuffer(uint initialSize = 16 * 1024)
    {
        EnsureCapacity(initialSize);
    }

    public byte* Pointer => _pointer;

    public uint Size { get; private set; }

    /// <summary>Grows the buffer to at least <paramref name="size"/> bytes (contents are not preserved).</summary>
    public void EnsureCapacity(uint size)
    {
        ObjectDisposedException.ThrowIf(Size == uint.MaxValue, this);
        if (size <= Size && _pointer != null)
        {
            return;
        }

        if (_pointer != null)
        {
            NativeMemory.Free(_pointer);
        }

        _pointer = (byte*)NativeMemory.AllocZeroed(size);
        Size = size;
    }

    public void Dispose()
    {
        if (_pointer != null)
        {
            NativeMemory.Free(_pointer);
            _pointer = null;
        }

        Size = uint.MaxValue;
    }
}
