using System;
using System.IO;
using Cosmos.Kernel.HAL.Interfaces.Devices;
using Cosmos.Kernel.HAL.Vfs;

namespace Zenith.Core.Storage.Dev;

/// <summary>Read-only byte-stream view of one disk or partition instance.</summary>
internal sealed class BlockDeviceNode : IVfsInode, IFileOperations
{
    private readonly IBlockDevice _device;
    private readonly Func<IBlockDevice, bool> _isPresent;
    private readonly int _sectorSize;
    private readonly long _length;
    private readonly ulong _number;

    public BlockDeviceNode(IBlockDevice device, ulong number, IInodeOperations operations, Func<IBlockDevice, bool> isPresent)
    {
        if (!IsSupported(device))
        {
            throw new ArgumentException("Device geometry cannot be represented as a byte stream");
        }
        _device = device;
        _sectorSize = (int)device.BlockSize;
        _length = (long)(device.BlockCount * device.BlockSize);
        _number = number;
        _isPresent = isPresent;
        Name = device.Name;
        InodeOperations = operations;
    }

    // Bound bounce-buffer allocation even if a driver reports unreasonable geometry.
    public static bool IsSupported(IBlockDevice device) => device.BlockSize > 0
        && device.BlockSize <= 1024 * 1024
        && device.BlockCount <= (ulong)long.MaxValue / device.BlockSize;

    public string Name { get; }
    public IInodeOperations InodeOperations { get; }
    public IFileOperations FileOperations => this;

    public bool TryStat(out VfsStat stat)
    {
        stat = default;
        if (!_isPresent(_device))
        {
            return false;
        }
        stat = new VfsStat
        {
            Ino = _number,
            Mode = VfsMode.BlockDevice | (VfsMode)0x124, // 0444
            NLink = 1,
            Size = (ulong)_length,
            BlkSize = _sectorSize,
            // Mount-local device IDs; preserve Cosmos names instead of inventing Linux aliases.
            Rdev = 0x800 + _number,
        };
        return true;
    }

    private void EnsurePresent()
    {
        if (!_isPresent(_device))
        {
            throw new IOException("Block device is no longer present: " + Name);
        }
    }

    public long Read(IVfsOpenFile openFile, Span<byte> buffer)
    {
        EnsurePresent();
        long position = openFile.Position;
        if (position < 0)
        {
            throw new IOException("Negative block device position");
        }
        if (position >= _length || buffer.IsEmpty)
        {
            return 0;
        }
        int count = (int)Math.Min(buffer.Length, _length - position);
        int copied = 0;
        byte[]? sector = null;
        while (copied < count)
        {
            EnsurePresent();
            ulong block = (ulong)(position / _sectorSize);
            int offset = (int)(position % _sectorSize);
            int remaining = count - copied;
            if (offset == 0 && remaining >= _sectorSize)
            {
                int bytes = remaining / _sectorSize * _sectorSize;
                _device.ReadBlock(block, (ulong)(bytes / _sectorSize), buffer.Slice(copied, bytes));
                copied += bytes;
                position += bytes;
            }
            else
            {
                sector ??= new byte[_sectorSize];
                _device.ReadBlock(block, 1, sector);
                int bytes = Math.Min(remaining, _sectorSize - offset);
                sector.AsSpan(offset, bytes).CopyTo(buffer.Slice(copied, bytes));
                copied += bytes;
                position += bytes;
            }
        }
        // The VFS advances openFile.Position, including for short reads at EOF.
        return copied;
    }

    public long Write(IVfsOpenFile openFile, ReadOnlySpan<byte> buffer)
        => throw new IOException("Raw block devices are read-only: " + Name);

    public bool Seek(IVfsOpenFile openFile, long offset, SeekWhence whence, out long newPosition)
    {
        newPosition = 0;
        EnsurePresent();
        long origin;
        switch (whence)
        {
            case SeekWhence.Set: origin = 0; break;
            case SeekWhence.Cur: origin = openFile.Position; break;
            case SeekWhence.End: origin = _length; break;
            default: return false;
        }
        if (origin < 0 || origin > _length || offset < -origin || offset > _length - origin)
        {
            return false;
        }
        newPosition = origin + offset;
        openFile.Position = newPosition;
        return true;
    }

    public bool Fsync(IVfsOpenFile openFile)
    {
        EnsurePresent();
        return true; // This view never writes or owns dirty data.
    }

    public void Release(IVfsOpenFile openFile) { }
}
