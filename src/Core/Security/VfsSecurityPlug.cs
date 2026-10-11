using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using Cosmos.Build.API.Attributes;
using Cosmos.Kernel.HAL.Vfs;
using Cosmos.Kernel.System.Vfs;

namespace Zenith.Core.Security;

/// <summary>Close path-layer gaps before driver callbacks: mount traversal and pending unlink.</summary>
[Plug(typeof(VfsManager))]
internal static class VfsSecurityPlug
{
    [PlugMember]
    public static void RemoveEntryDirect(string fullPath)
    {
        // The path was authorized before MarkPendingIfOpen. Final-close cleanup
        // must survive a subsequent identity/mode change of the closing handle.
        using var root = CredentialContext.Enter(Credentials.Root);
        string parentPath = Path.GetDirectoryName(fullPath) ?? "/";
        if (VfsManager.TryOpenDirectory(parentPath, out var parent))
        {
            using (parent) { parent.TryUnlink(Path.GetFileName(fullPath)); }
        }
    }
    [PlugMember]
    public static bool TryUnlink(string fullPath)
    {
        string parentPath = Path.GetDirectoryName(fullPath) ?? "/";
        string name = Path.GetFileName(fullPath);
        if (!VfsManager.TryOpenDirectory(parentPath, out var parent)) { return false; }
        using (parent)
        {
            FileAccessPolicy.RequireRemoval(parent.Inode, name);
            // Authorize BEFORE Cosmos can mark an open handle delete-pending.
            if (FileAccessPolicy.MarkPending(null, fullPath)) { return true; }
            return parent.TryUnlink(name);
        }
    }

    [PlugMember]
    public static bool TryResolve(string path, [NotNullWhen(true)] out IVfsInode? inode,
        [NotNullWhen(true)] out string? leafName)
    {
        inode = null; leafName = null;
        if (string.IsNullOrEmpty(path) || path[0] != '/') { return false; }
        // Start at / on each link hop so mounts cannot bypass inaccessible ancestors.
        for (int hops = 0; hops <= 8; hops++)
        {
            IVfsSuperblock? root = null;
            foreach (var mount in VfsManager.Mounts) { if (mount.MountPoint == "/") { root = mount.Superblock; break; } }
            if (root is null) { return false; }
            IVfsInode current = root.Root;
            string parentPath = "/";
            string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            bool restart = false;
            leafName = "/";
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (part == ".") { continue; }
                if (part == "..") { return false; }
                FileAccessPolicy.Require(current, PermissionPolicy.Search);
                if (!current.InodeOperations.Lookup(current, part, out var child)
                    || !child.InodeOperations.GetAttr(child, out var stat)) { return false; }
                if (stat.IsSymbolicLink)
                {
                    if (hops == 8 || !child.InodeOperations.TryReadLink(child, out var target) || string.IsNullOrEmpty(target)) { return false; }
                    string resolved = target[0] == '/' ? target : parentPath.TrimEnd('/') + "/" + target;
                    if (i + 1 < parts.Length) { resolved += "/" + string.Join("/", parts, i + 1, parts.Length - i - 1); }
                    path = resolved; restart = true; break;
                }
                string full = parentPath.TrimEnd('/') + "/" + part;
                foreach (var mount in VfsManager.Mounts)
                {
                    if (mount.MountPoint == full)
                    {
                        // The covered directory's search bit applies at a mount boundary.
                        FileAccessPolicy.Require(child, PermissionPolicy.Search);
                        child = mount.Superblock.Root; break;
                    }
                }
                current = child; parentPath = full; leafName = part;
            }
            if (restart) { continue; }
            inode = current; return true;
        }
        return false;
    }
}
