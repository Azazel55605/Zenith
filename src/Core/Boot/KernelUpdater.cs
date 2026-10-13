using System;
using System.IO;
using System.Text;
using Zenith.Core.Packages;

namespace Zenith.Core.Boot;

internal interface IKernelUpdateStore
{
    bool Exists(string path);
    byte[] Read(string path, int maximum);
    void Write(string path, byte[] bytes);
    void Delete(string path);
    void Directory(string path);
    void Sync();
    void RecoverWrites();
    ulong FreeEspBytes { get; }
}

/// <summary>Kernel-only coordinator. A durable undo state precedes every boot-menu mutation.</summary>
internal sealed class KernelUpdater
{
    public const string EspPrefix = "/boot/efi";
    public const string StatePath = "/var/lib/zenith/boot/generations.tsv";
    public const string Journal = "/var/lib/zenith/boot/update.tsv";
    public const string MenuPath = EspPrefix + "/limine.conf";
    public const string FallbackMenu = EspPrefix + "/boot/limine/limine.conf";
    public const string RootInventory = "/var/lib/zenith/boot/installed.tsv";
    public const string EspInventory = EspPrefix + "/boot/zenith/manifest.tsv";
    private readonly IKernelUpdateStore _store;
    private readonly Guid _root, _esp;
    public KernelUpdater(IKernelUpdateStore store, Guid root, Guid esp) { _store = store; _root = root; _esp = esp; }
    private string Text(string path, int maximum = 4096) => new UTF8Encoding(false, true).GetString(_store.Read(path, maximum));
    private void Text(string path, string text) => _store.Write(path, Encoding.UTF8.GetBytes(text));
    private void Bind(KernelUpdateState state)
    {
        if (state.Root != _root || state.Esp != _esp || _root == Guid.Empty || _esp == Guid.Empty) { throw new IOException("boot metadata does not match mounted root/ESP UUIDs"); }
    }
    public KernelUpdateState Status()
    {
        var state = _store.Exists(StatePath) ? KernelUpdateState.Parse(Text(StatePath)) : KernelUpdateState.Baseline(Text(RootInventory));
        Bind(state); return state;
    }
    public bool RecoveryRequired => _store.Exists(Journal) || _store.Exists(Journal + ".zenith-new");
    private void VacantWorkFiles(string path)
    {
        if (_store.Exists(path + ".zenith-new") || _store.Exists(path + ".zenith-old")) { throw new IOException("occupied boot staging path"); }
    }
    private void Idle()
    {
        if (RecoveryRequired) { throw new IOException("interrupted kernel update: run boot recover first"); }
        foreach (string path in new[] { StatePath, RootInventory, EspInventory, MenuPath, FallbackMenu }) { VacantWorkFiles(path); }
        if (_store.Exists("/var/lib/zenith/pkg/journal.tsv")) { throw new IOException("pending data package transaction: run pkg recover first"); }
    }
    private void Verify(KernelGeneration generation)
    {
        byte[] bytes = _store.Read(EspPrefix + BootLayout.KernelPath(generation.Id), BootLayout.MaximumKernelBytes);
        BootLayout.ValidateKernel(bytes);
        if (bytes.Length != generation.Bytes || BootLayout.Digest(bytes) != generation.Id) { throw new IOException("retained kernel integrity mismatch"); }
    }
    private void VerifyEfi(KernelUpdateState state)
    {
        byte[] bytes = _store.Read(EspPrefix + "/EFI/BOOT/BOOTX64.EFI", BootLayout.MaximumEfiBytes);
        BootLayout.ValidateEfi(bytes);
        if (bytes.Length != state.EfiBytes || BootLayout.Digest(bytes) != state.EfiId) { throw new IOException("EFI loader integrity mismatch"); }
    }
    private void Publish(KernelUpdateState state)
    {
        // Payloads already flushed. Metadata first, menu last; journal stays through all phases.
        Text(StatePath, state.Serialize());
        Text(RootInventory, state.Inventory()); Text(EspInventory, state.Inventory()); _store.Sync();
        Text(MenuPath, state.Menu()); _store.Sync();
        // Limine can prefer the secondary search path even when the primary exists.
        // Retain the old secondary through the primary gap, then commit both menus.
        Text(FallbackMenu, state.Menu()); _store.Sync();
    }
    private void Begin(KernelUpdateState before, string? candidate = null)
    {
        Text(Journal, "ZENITH-KERNEL-UNDO\t1\ncandidate\t" + (candidate ?? "-") + "\n" + before.Serialize()); _store.Sync();
        _store.Directory(EspPrefix + "/boot/limine");
        // Limine's secondary search path remains bootable during root-menu rename gaps.
        Text(FallbackMenu, before.Menu()); _store.Sync();
    }
    private void End() { _store.Delete(Journal); _store.Sync(); }
    public KernelUpdateState CheckCandidate(KernelPackage package, string running)
    {
        Idle(); KernelPackage.ValidatePayload(package.Kernel);
        if (package.Id != BootLayout.Digest(package.Kernel)) { throw new IOException("candidate digest mismatch"); }
        var before = Status();
        if (before.Pending || before.Retired is not null) { throw new IOException("confirm or roll back the pending generation before another update"); }
        if (running != before.Current.Id) { throw new IOException("boot the active installed kernel before upgrading"); }
        if (PackageVersions.Compare(package.Version, before.HighWater) <= 0 || package.Id == before.Current.Id || package.Id == before.Previous?.Id)
        { throw new IOException("kernel downgrade, repeated version or reused generation refused"); }
        Verify(before.Current); if (before.Previous is not null) { Verify(before.Previous); } VerifyEfi(before);
        if (_store.FreeEspBytes < (ulong)package.Kernel.Length + 1024 * 1024) { throw new IOException("insufficient EFI free space; previous kernel retained"); }
        string path = EspPrefix + BootLayout.KernelPath(package.Id);
        if (_store.Exists(path)) { throw new IOException("unowned/abandoned candidate path exists"); }
        VacantWorkFiles(path); VacantWorkFiles(EspPrefix + "/boot/zenith/" + package.Id + "/manifest.tsv");
        if (_store.Exists(EspPrefix + "/boot/zenith/" + package.Id + "/manifest.tsv")) { throw new IOException("occupied generation metadata"); }
        return before;
    }
    public void Install(KernelPackage package, string running, Action cancel, Action<string> progress)
    {
        var before = CheckCandidate(package, running);
        string path = EspPrefix + BootLayout.KernelPath(package.Id);
        cancel(); Begin(before, package.Id);
        progress("Staging signed kernel " + package.Version);
        _store.Directory(EspPrefix + "/boot/zenith/" + package.Id);
        _store.Write(path, package.Kernel);
        Verify(new(package.Id, package.Kernel.Length, package.Version)); _store.Sync();
        cancel();
        var after = before with { Current = new(package.Id, package.Kernel.Length, package.Version), Previous = before.Current,
            Retired = before.Previous, Pending = true, HighWater = package.Version };
        Text(EspPrefix + "/boot/zenith/" + package.Id + "/manifest.tsv", after.Inventory()); _store.Sync();
        // No cancellation inside activation: recover the durable undo journal after any failure.
        progress("Publishing next-boot generation"); Publish(after); End();
        progress("Kernel staged; running kernel unchanged. Reboot, test, then boot confirm.");
    }
    public void Recover()
    {
        if (!RecoveryRequired) { return; }
        string journal = Text(_store.Exists(Journal) ? Journal : Journal + ".zenith-new", 8192); int start = journal.IndexOf("\n", journal.IndexOf("\n") + 1) + 1;
        string[] fields = journal[..start].Split('\n');
        if (fields.Length != 3 || fields[0] != "ZENITH-KERNEL-UNDO\t1" || !fields[1].StartsWith("candidate\t", StringComparison.Ordinal))
        { throw new IOException("invalid kernel undo journal"); }
        string candidate = fields[1][10..];
        if (candidate != "-" && !BootLayout.ValidId(candidate)) { throw new IOException("invalid undo candidate"); }
        var before = KernelUpdateState.Parse(journal[start..]); Bind(before);
        if (candidate == before.Current.Id || candidate == before.Previous?.Id || candidate == before.Retired?.Id) { throw new IOException("undo candidate overlaps retained generation"); }
        Verify(before.Current); if (before.Previous is not null) { Verify(before.Previous); } VerifyEfi(before);
        _store.RecoverWrites(); Publish(before); Text(FallbackMenu, before.Menu()); _store.Sync();
        if (candidate != "-")
        {
            _store.Delete(EspPrefix + BootLayout.KernelPath(candidate));
            _store.Delete(EspPrefix + "/boot/zenith/" + candidate + "/manifest.tsv");
        }
        _store.Delete(Journal + ".zenith-new"); _store.Sync(); End();
    }
    public void Confirm(string running)
    {
        Idle(); var state = Status();
        if (running != state.Current.Id) { throw new IOException("only the active running kernel can be confirmed"); }
        Verify(state.Current); if (state.Previous is not null) { Verify(state.Previous); } VerifyEfi(state);
        // Keep the retained previous generation. Remove only the explicitly recorded third generation.
        if (state.Retired is not null)
        {
            _store.Delete(EspPrefix + BootLayout.KernelPath(state.Retired.Id));
            _store.Delete(EspPrefix + "/boot/zenith/" + state.Retired.Id + "/manifest.tsv"); _store.Sync();
        }
        Begin(state with { Retired = null });
        var confirmed = state with { Pending = false, Retired = null }; Publish(confirmed);
        End();
    }
    public void Rollback()
    {
        Idle(); var state = Status();
        if (state.Previous is null) { throw new IOException("no retained previous kernel"); }
        Verify(state.Previous); VerifyEfi(state); Begin(state);
        Text(FallbackMenu, BootLayout.Configuration(state.Root, state.Previous.Id)); _store.Sync();
        var rolled = state with { Current = state.Previous, Previous = state.Retired, Retired = state.Current, Pending = false };
        Publish(rolled); Text(FallbackMenu, rolled.Menu()); _store.Sync(); End();
    }
}
