using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Zenith.Core.Boot;

/// <summary>Read-only bootloader-owned memory. Cosmos only allocates Usable regions.</summary>
internal sealed unsafe class NativeBootAssets
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BootFile { public ulong Revision; public byte* Address; public ulong Size; public byte* Path; public byte* String; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExecutableResponse { public ulong Revision; public BootFile* File; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleResponse { public ulong Revision; public ulong Count; public BootFile** Files; }
    [DllImport("*", EntryPoint = "zenith_boot_executable")]
    private static extern ExecutableResponse* Executable();
    [DllImport("*", EntryPoint = "zenith_boot_modules")]
    private static extern ModuleResponse* Modules();
    private readonly BootFile* _kernel;
    private readonly BootFile* _efi;
    public ReadOnlySpan<byte> Kernel => new(_kernel->Address, (int)_kernel->Size);
    public ReadOnlySpan<byte> Efi => new(_efi->Address, (int)_efi->Size);
    public string KernelId { get; }
    public string EfiId { get; }
    private NativeBootAssets(BootFile* kernel, BootFile* efi)
    {
        _kernel = kernel; _efi = efi;
        BootLayout.ValidateKernel(Kernel); BootLayout.ValidateEfi(Efi);
        KernelId = BootLayout.Digest(Kernel); EfiId = BootLayout.Digest(Efi);
    }
    private static BootFile* KernelFile()
    {
        var response = Executable();
        if (response == null || response->File == null || response->File->Address == null
            || response->File->Size < 64 || response->File->Size > BootLayout.MaximumKernelBytes)
        { throw new IOException("bootloader did not supply a bounded kernel file; use the installation ISO"); }
        return response->File;
    }
    public static string RunningKernelId()
    {
        var file = KernelFile(); var bytes = new ReadOnlySpan<byte>(file->Address, (int)file->Size);
        BootLayout.ValidateKernel(bytes); return BootLayout.Digest(bytes);
    }
    public static NativeBootAssets Open()
    {
        var modules = Modules(); BootFile* efi = null;
        if (modules == null || modules->Files == null || modules->Count > 16) { throw new IOException("UEFI installation module unavailable"); }
        for (ulong i = 0; i < modules->Count; i++)
        {
            var file = modules->Files[i]; if (file == null || file->String == null) { continue; }
            const string name = "zenith-installer-efi"; int n = 0;
            while (n < name.Length && file->String[n] == name[n]) { n++; }
            if (n != name.Length || file->String[n] != 0) { continue; }
            if (efi != null || file->Address == null || file->Size > BootLayout.MaximumEfiBytes || file->Size < 256)
            { throw new IOException("invalid/duplicate UEFI installation module"); }
            efi = file;
        }
        if (efi == null) { throw new IOException("UEFI installation module missing; disk has not been erased"); }
        return new(KernelFile(), efi);
    }
}
