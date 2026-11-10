using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Read-only consistency audit of the supported ext2 profiles. Never repairs. Keeps two bits per
/// block, one bit and two bytes per inode, and a map of directory parents in memory (64 MiB per TiB of
/// 4 KiB blocks); a volume that needs more than <see cref="AvailableMemory"/> is refused, not half-checked.</summary>
internal sealed class Ext2Checker
{
    /// <summary>Bytes the check may use; the kernel bounds it by free memory.</summary>
    public static Func<ulong> AvailableMemory { get; set; } = () => ulong.MaxValue;

    private readonly Ext2Superblock _sb;
    private readonly Ext2Geometry _geometry;
    private readonly ulong[] _used, _referenced, _inodes;
    private readonly ushort[] _links;
    private readonly Dictionary<uint, uint> _parents = new(), _dotParents = new();
    private readonly List<string> _errors = new();
    private int _errorCount;
    private uint _files, _dirs;
    private Ext2Checker(Ext2Superblock sb, Ext2Geometry geometry)
    {
        _sb = sb; _geometry = geometry;
        _used = new ulong[(sb.BlocksCount + 63) / 64]; _referenced = new ulong[(sb.BlocksCount + 63) / 64];
        _inodes = new ulong[(sb.InodesCount + 64) / 64]; _links = new ushort[sb.InodesCount + 1];
    }

    /// <summary>Memory the check needs for a volume of this size.</summary>
    public static ulong RequiredMemory(uint blocks, uint inodes) => (ulong)blocks / 4 + (ulong)inodes / 8 + (ulong)inodes * 2 + 1024 * 1024;

    public static bool Check(IBlockDevice device, out string report, bool allowUnclean = false)
    {
        try
        {
            if (device.BlockSize == 0 || device.BlockCount > Ext2Formatter.MaximumBytes / device.BlockSize)
            {
                report = "check supports partitions up to 2 TiB"; return false;
            }
            if (Ext2VolumePolicy.Check(device, allowUnclean) is string profile)
            {
                report = profile; return false;
            }
            if (!Ext2Superblock.TryCreate(device, out var sb)) { report = "invalid ext2 metadata"; return false; }
            var geometry = new Ext2Geometry(sb.BlockSize, sb.BlocksCount, sb.BlocksPerGroup, sb.InodesPerGroup,
                (sb.FeatureRoCompat & Ext2SuperblockLayout.RoCompatSparseSuper) != 0);
            if (sb.FirstIno != 11 || sb.InodeSize != Ext2Geometry.InodeSize || sb.InodesCount != geometry.Inodes
                || geometry.Groups != sb.GroupsCount || sb.FirstDataBlock != geometry.FirstDataBlock)
            { report = "unsupported inode geometry; no changes made"; return false; }
            ulong needed = RequiredMemory(sb.BlocksCount, sb.InodesCount);
            if (needed > AvailableMemory())
            {
                report = "checking this volume needs " + needed / (1024 * 1024) + " MiB of free memory; not enough available; no changes made";
                return false;
            }
            var checker = new Ext2Checker(sb, geometry);
            checker.Run();
            report = checker._errorCount == 0
                ? "clean: " + checker._files + " files, " + checker._dirs + " directories; no changes made"
                : string.Join("\n", checker._errors) + "\n" + checker._errorCount + " errors; no changes made";
            return checker._errorCount == 0;
        }
        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is OverflowException)
        {
            report = "invalid or unreadable metadata: " + ex.Message + "; no changes made"; return false;
        }
    }

    private void Error(string text)
    {
        _errorCount++;
        if (_errors.Count < 32) { _errors.Add(text); }
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static bool Bit(byte[] map, uint bit) => (map[bit / 8] & (1 << (int)(bit % 8))) != 0;
    private static bool Get(ulong[] set, uint index) => (set[index / 64] & (1UL << (int)(index % 64))) != 0;
    private static void Set(ulong[] set, uint index) => set[index / 64] |= 1UL << (int)(index % 64);
    private bool Own(uint block)
    {
        if (block < _sb.FirstDataBlock || block >= _sb.BlocksCount) { Error("out-of-range block " + block); return false; }
        if (Get(_referenced, block)) { Error("duplicate block " + block); return false; }
        Set(_referenced, block);
        if (!Get(_used, block)) { Error("referenced free block " + block); }
        return true;
    }
    private void Run()
    {
        uint freeBlocks = 0, freeInodes = 0, bs = _sb.BlockSize, bits = bs * 8;
        var g = _geometry;
        byte[] bitmap = new byte[bs];
        for (uint group = 0; group < _sb.GroupsCount; group++)
        {
            var gd = _sb.Group(group);
            uint first = g.GroupStart(group), count = g.GroupBlocks(group);
            if (gd.BlockBitmap < first || gd.BlockBitmap >= first + count
                || gd.InodeBitmap < first || gd.InodeBitmap >= first + count
                || gd.InodeTable < first || gd.InodeTable + g.TableBlocks > first + count)
            { throw new IOException("group metadata outside its group"); }
            _sb.ReadBlocks(gd.BlockBitmap, 1, bitmap);
            uint groupFree = 0;
            for (uint i = 0; i < count; i++) { if (Bit(bitmap, i)) { Set(_used, first + i); } else { groupFree++; } }
            for (uint i = count; i < bits; i++) { if (!Bit(bitmap, i)) { Error("unmarked block bitmap padding in group " + group); break; } }
            freeBlocks += groupFree;
            if (groupFree != gd.FreeBlocksCount) { Error("free block count mismatch in group " + group); }
            if (g.HasBackup(group)) { for (uint i = 0; i <= g.DescriptorBlocks; i++) { Own(first + i); } }
            Own(gd.BlockBitmap); Own(gd.InodeBitmap);
            for (uint i = 0; i < g.TableBlocks; i++) { Own(gd.InodeTable + i); }
            _sb.ReadBlocks(gd.InodeBitmap, 1, bitmap);
            uint inodeCount = Math.Min(_sb.InodesPerGroup, _sb.InodesCount - group * _sb.InodesPerGroup);
            groupFree = 0;
            for (uint i = 0; i < inodeCount; i++) { if (Bit(bitmap, i)) { Set(_inodes, group * _sb.InodesPerGroup + i + 1); } else { groupFree++; } }
            for (uint i = inodeCount; i < bits; i++) { if (!Bit(bitmap, i)) { Error("unmarked inode bitmap padding in group " + group); break; } }
            freeInodes += groupFree;
            if (groupFree != gd.FreeInodesCount) { Error("free inode count mismatch in group " + group); }
        }
        if (freeBlocks != _sb.FreeBlocksCount || freeInodes != _sb.FreeInodesCount) { Error("superblock free counts mismatch"); }
        for (uint n = 1; n < _sb.FirstIno; n++) { if (!Get(_inodes, n)) { Error("free reserved inode " + n); } }
        var groupDirs = new uint[_sb.GroupsCount];
        if (!Get(_inodes, 2) || !_sb.ReadInode(2, "/").IsDirectory) { Error("invalid root directory"); }
        for (uint n = 2; n <= _sb.InodesCount; n++)
        {
            if (!Get(_inodes, n) || (n < _sb.FirstIno && n != 2)) { continue; }
            var node = _sb.ReadInode(n, "");
            if (!node.IsDirectory && !node.IsRegularFile && !node.IsSymlink) { Error("unsupported inode type " + n); continue; }
            if (node.IsDirectory) { _dirs++; groupDirs[_sb.GroupOfInode(n)]++; } else { _files++; }
            if (node.Dtime != 0) { Error("allocated inode has deletion time " + n); }
            if (node.FullSize > _sb.MaxFileSize) { Error("file exceeds supported size in inode " + n); }
            ulong allocated = 0;
            if (!node.IsSymlink || node.Blocks != 0)
            {
                for (int i = 0; i < 15; i++) { allocated += Walk(node.Block[i], i < 12 ? 0 : i - 11); }
            }
            if (allocated * (bs / 512) != node.Blocks) { Error("sector count mismatch in inode " + n); }
            if (node.IsSymlink && !Ext2FileOperations.TryReadSymlinkTarget(node, out _)) { Error("invalid symbolic link " + n); }
            if (node.IsDirectory) { Directory(node); }
        }
        for (uint n = 2; n <= _sb.InodesCount; n++)
        {
            if (!Get(_inodes, n) || (n < _sb.FirstIno && n != 2)) { continue; }
            var node = _sb.ReadInode(n, "");
            if (_links[n] != node.LinksCount || _links[n] == 0) { Error("link count mismatch or orphan inode " + n); }
            if (!node.IsDirectory) { continue; }
            uint parent = _parents.TryGetValue(n, out uint p) ? p : 0, dotParent = _dotParents.TryGetValue(n, out uint d) ? d : 0;
            if ((n == 2 && dotParent != 2) || (n != 2 && dotParent != parent)) { Error("wrong directory parent " + n); }
            var seen = new HashSet<uint>();
            uint cursor = n;
            while (cursor != 2 && cursor != 0 && seen.Count < 1024 && seen.Add(cursor)) { cursor = _parents.TryGetValue(cursor, out uint up) ? up : 0; }
            if (cursor != 2) { Error("disconnected or cyclic directory " + n); }
        }
        for (uint group = 0; group < _sb.GroupsCount; group++)
        {
            if (groupDirs[group] != _sb.Group(group).UsedDirsCount) { Error("directory count mismatch in group " + group); }
        }
        // Allocated but unreferenced blocks, a word of 64 blocks at a time.
        for (uint word = 0; word < _used.Length; word++)
        {
            ulong orphans = _used[word] & ~_referenced[word];
            while (orphans != 0)
            {
                uint block = word * 64 + (uint)System.Numerics.BitOperations.TrailingZeroCount(orphans);
                orphans &= orphans - 1;
                if (block >= _sb.FirstDataBlock && block < _sb.BlocksCount) { Error("allocated unreferenced block " + block); }
            }
        }
    }
    private ulong Walk(uint block, int depth)
    {
        if (block == 0 || !Own(block)) { return 0; }
        ulong count = 1;
        if (depth == 0) { return count; }
        uint bs = _sb.BlockSize;
        byte[] data = new byte[bs]; _sb.ReadBlocks(block, 1, data);
        for (int i = 0; i < bs; i += 4) { count += Walk(U32(data, i), depth - 1); }
        return count;
    }
    private void Directory(Ext2Inode node)
    {
        uint bs = _sb.BlockSize;
        if (node.FullSize == 0 || node.FullSize % bs != 0 || node.FullSize > 4 * 1024 * 1024)
        { Error("invalid directory size " + node.InodeNumber); return; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool dot = false, dotdot = false;
        byte[] data = new byte[bs];
        for (uint index = 0; index < node.FullSize / bs; index++)
        {
            uint block = _sb.GetBlockPointer(node, index, false, out _);
            if (block == 0) { Error("directory hole " + node.InodeNumber); continue; }
            _sb.ReadBlocks(block, 1, data);
            for (int p = 0; p < bs;)
            {
                if (p + 8 > bs) { Error("short directory record"); break; }
                uint ino = U32(data, p);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4));
                int nameLength = data[p + 6];
                if (length < 8 || length % 4 != 0 || p + length > bs || nameLength > length - 8)
                { Error("invalid directory record in " + node.InodeNumber); break; }
                if (ino != 0)
                {
                    string name = Encoding.UTF8.GetString(data, p + 8, nameLength);
                    if (!names.Add(name)) { Error("duplicate directory name in " + node.InodeNumber); }
                    if (ino > _sb.InodesCount || !Get(_inodes, ino)) { Error("directory references free/invalid inode " + ino); }
                    else
                    {
                        if (_links[ino] == ushort.MaxValue) { Error("link count overflow " + ino); } else { _links[ino]++; }
                        var child = _sb.ReadInode(ino, name);
                        if (data[p + 7] != Ext2DirectoryHelper.FileTypeFromMode(child.Mode)) { Error("directory entry type mismatch"); }
                        if (name == ".") { dot = true; if (ino != node.InodeNumber) { Error("wrong '.' inode"); } }
                        else if (name == "..") { dotdot = true; _dotParents[node.InodeNumber] = ino; if (!child.IsDirectory) { Error("non-directory parent"); } }
                        else
                        {
                            if (!Ext2DirectoryHelper.IsValidName(name)) { Error("invalid directory name"); }
                            if (child.IsDirectory)
                            {
                                if (ino == 2) { Error("root has a named parent"); }
                                if (_parents.ContainsKey(ino)) { Error("multiple directory parents"); }
                                _parents[ino] = node.InodeNumber;
                            }
                        }
                    }
                }
                p += length;
            }
        }
        if (!dot || !dotdot) { Error("missing dot entries in " + node.InodeNumber); }
    }
}
