using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Cosmos.Kernel.HAL.Interfaces.Devices;

namespace Zenith.Core.Storage.Ext2;

/// <summary>Read-only consistency audit of the supported bounded ext2 profile. Never repairs.</summary>
internal sealed class Ext2Checker
{
    private readonly Ext2Superblock _sb;
    private readonly bool[] _blocks, _inodes;
    private readonly uint[] _owners, _links, _parents, _dotParents;
    private readonly List<string> _errors = new();
    private int _errorCount;
    private uint _files, _dirs;
    private Ext2Checker(Ext2Superblock sb)
    {
        _sb = sb;
        _blocks = new bool[sb.BlocksCount]; _owners = new uint[sb.BlocksCount];
        _inodes = new bool[sb.InodesCount + 1]; _links = new uint[sb.InodesCount + 1];
        _parents = new uint[sb.InodesCount + 1]; _dotParents = new uint[sb.InodesCount + 1];
    }

    public static bool Check(IBlockDevice device, out string report, bool allowUnclean = false)
    {
        try
        {
            if (device.BlockSize == 0 || device.BlockCount > Ext2Formatter.MaximumBytes / device.BlockSize)
            {
                report = "check supports partitions up to 512 MiB"; return false;
            }
            if (Ext2VolumePolicy.Check(device, allowUnclean) is string profile)
            {
                report = profile; return false;
            }
            if (!Ext2Superblock.TryCreate(device, out var sb)) { report = "invalid ext2 metadata"; return false; }
            if (sb.InodesCount > 65536 || sb.FirstIno != 11
                || (sb.InodesCount + sb.InodesPerGroup - 1) / sb.InodesPerGroup != sb.GroupsCount)
            { report = "unsupported inode geometry; no changes made"; return false; }
            var checker = new Ext2Checker(sb);
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
    private bool Own(uint block, uint owner)
    {
        if (block < _sb.FirstDataBlock || block >= _sb.BlocksCount) { Error("out-of-range block " + block); return false; }
        if (_owners[block] != 0) { Error("duplicate block " + block); return false; }
        _owners[block] = owner;
        if (!_blocks[block]) { Error("referenced free block " + block); }
        return true;
    }
    private void Run()
    {
        uint freeBlocks = 0, freeInodes = 0;
        uint descriptorBlocks = (_sb.GroupsCount * 32 + 1023) / 1024;
        byte[] bitmap = new byte[1024];
        for (uint g = 0; g < _sb.GroupsCount; g++)
        {
            var gd = _sb.Group(g);
            uint first = 1 + g * _sb.BlocksPerGroup;
            uint count = Math.Min(_sb.BlocksPerGroup, _sb.BlocksCount - first);
            if (gd.BlockBitmap < first || gd.BlockBitmap >= first + count
                || gd.InodeBitmap < first || gd.InodeBitmap >= first + count
                || gd.InodeTable < first || gd.InodeTable + (_sb.InodesPerGroup * 128 + 1023) / 1024 > first + count)
            { throw new IOException("group metadata outside its group"); }
            _sb.ReadBlocks(gd.BlockBitmap, 1, bitmap);
            uint groupFree = 0;
            for (uint i = 0; i < count; i++) { _blocks[first + i] = Bit(bitmap, i); if (!_blocks[first + i]) { groupFree++; } }
            for (uint i = count; i < 8192; i++) { if (!Bit(bitmap, i)) { Error("unmarked block bitmap padding in group " + g); break; } }
            freeBlocks += groupFree;
            if (groupFree != gd.FreeBlocksCount) { Error("free block count mismatch in group " + g); }
            for (uint i = 0; i <= descriptorBlocks; i++) { Own(first + i, uint.MaxValue); }
            Own(gd.BlockBitmap, uint.MaxValue); Own(gd.InodeBitmap, uint.MaxValue);
            for (uint i = 0; i < (_sb.InodesPerGroup * 128 + 1023) / 1024; i++) { Own(gd.InodeTable + i, uint.MaxValue); }
            _sb.ReadBlocks(gd.InodeBitmap, 1, bitmap);
            uint inodeCount = Math.Min(_sb.InodesPerGroup, _sb.InodesCount - g * _sb.InodesPerGroup);
            groupFree = 0;
            for (uint i = 0; i < inodeCount; i++) { _inodes[g * _sb.InodesPerGroup + i + 1] = Bit(bitmap, i); if (!Bit(bitmap, i)) { groupFree++; } }
            for (uint i = inodeCount; i < 8192; i++) { if (!Bit(bitmap, i)) { Error("unmarked inode bitmap padding in group " + g); break; } }
            freeInodes += groupFree;
            if (groupFree != gd.FreeInodesCount) { Error("free inode count mismatch in group " + g); }
        }
        if (freeBlocks != _sb.FreeBlocksCount || freeInodes != _sb.FreeInodesCount) { Error("superblock free counts mismatch"); }
        for (uint n = 1; n < _sb.FirstIno; n++) { if (!_inodes[n]) { Error("free reserved inode " + n); } }
        var groupDirs = new uint[_sb.GroupsCount];
        if (!_inodes[2] || !_sb.ReadInode(2, "/").IsDirectory) { Error("invalid root directory"); }
        for (uint n = 2; n <= _sb.InodesCount; n++)
        {
            if (!_inodes[n] || (n < _sb.FirstIno && n != 2)) { continue; }
            var node = _sb.ReadInode(n, "");
            if (!node.IsDirectory && !node.IsRegularFile && !node.IsSymlink) { Error("unsupported inode type " + n); continue; }
            if (node.IsDirectory) { _dirs++; groupDirs[_sb.GroupOfInode(n)]++; } else { _files++; }
            if (node.Dtime != 0) { Error("allocated inode has deletion time " + n); }
            if (node.FullSize > _sb.MaxFileSize) { Error("file exceeds supported size in inode " + n); }
            uint allocated = 0;
            if (!node.IsSymlink || node.Blocks != 0)
            {
                for (int i = 0; i < 15; i++) { allocated += Walk(node.Block[i], n, i < 12 ? 0 : i - 11); }
            }
            if (allocated * 2 != node.Blocks) { Error("sector count mismatch in inode " + n); }
            if (node.IsSymlink && !Ext2FileOperations.TryReadSymlinkTarget(node, out _)) { Error("invalid symbolic link " + n); }
            if (node.IsDirectory) { Directory(node); }
        }
        for (uint n = 2; n <= _sb.InodesCount; n++)
        {
            if (!_inodes[n] || (n < _sb.FirstIno && n != 2)) { continue; }
            var node = _sb.ReadInode(n, "");
            if (_links[n] != node.LinksCount || _links[n] == 0) { Error("link count mismatch or orphan inode " + n); }
            if (!node.IsDirectory) { continue; }
            if ((n == 2 && _dotParents[n] != 2) || (n != 2 && _dotParents[n] != _parents[n])) { Error("wrong directory parent " + n); }
            var seen = new HashSet<uint>();
            uint cursor = n;
            while (cursor != 2 && cursor != 0 && seen.Count < 1024 && seen.Add(cursor)) { cursor = _parents[cursor]; }
            if (cursor != 2) { Error("disconnected or cyclic directory " + n); }
        }
        for (uint g = 0; g < _sb.GroupsCount; g++)
        {
            if (groupDirs[g] != _sb.Group(g).UsedDirsCount) { Error("directory count mismatch in group " + g); }
        }
        for (uint b = 1; b < _sb.BlocksCount; b++) { if (_blocks[b] && _owners[b] == 0) { Error("allocated unreferenced block " + b); } }
    }
    private uint Walk(uint block, uint inode, int depth)
    {
        if (block == 0 || !Own(block, inode)) { return 0; }
        uint count = 1;
        if (depth == 0) { return count; }
        byte[] data = new byte[1024]; _sb.ReadBlocks(block, 1, data);
        for (int i = 0; i < 1024; i += 4) { count += Walk(U32(data, i), inode, depth - 1); }
        return count;
    }
    private void Directory(Ext2Inode node)
    {
        if (node.FullSize == 0 || node.FullSize % 1024 != 0 || node.FullSize > 4 * 1024 * 1024)
        { Error("invalid directory size " + node.InodeNumber); return; }
        var names = new HashSet<string>(StringComparer.Ordinal);
        bool dot = false, dotdot = false;
        byte[] data = new byte[1024];
        for (uint index = 0; index < node.FullSize / 1024; index++)
        {
            uint block = _sb.GetBlockPointer(node, index, false, out _);
            if (block == 0) { Error("directory hole " + node.InodeNumber); continue; }
            _sb.ReadBlocks(block, 1, data);
            for (int p = 0; p < 1024;)
            {
                if (p + 8 > 1024) { Error("short directory record"); break; }
                uint ino = U32(data, p);
                int length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p + 4));
                int nameLength = data[p + 6];
                if (length < 8 || length % 4 != 0 || p + length > 1024 || nameLength > length - 8)
                { Error("invalid directory record in " + node.InodeNumber); break; }
                if (ino != 0)
                {
                    string name = Encoding.UTF8.GetString(data, p + 8, nameLength);
                    if (!names.Add(name)) { Error("duplicate directory name in " + node.InodeNumber); }
                    if (ino > _sb.InodesCount || !_inodes[ino]) { Error("directory references free/invalid inode " + ino); }
                    else
                    {
                        _links[ino]++;
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
                                if (_parents[ino] != 0) { Error("multiple directory parents"); }
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
