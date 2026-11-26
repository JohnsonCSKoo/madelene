using System.IO.MemoryMappedFiles;

namespace Madelene.Storage;

/// <summary>Read-only memory map of a whole file with a raw base pointer.</summary>
internal sealed unsafe class MappedFile : IDisposable
{
    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private byte* _pointer;

    public MappedFile(string path)
    {
        Path = path;
        Length = new FileInfo(path).Length;
        if (Length == 0)
            return;
        _map = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        _view = _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
        _pointer += _view.PointerOffset;
    }

    public string Path { get; }

    public long Length { get; }

    public byte* Pointer => _pointer;

    public T* As<T>() where T : unmanaged => (T*)_pointer;

    public void Dispose()
    {
        if (_view != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }

        _map?.Dispose();
        _pointer = null;
    }
}
