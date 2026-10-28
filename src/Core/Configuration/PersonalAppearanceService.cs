using System;
using System.Collections.Generic;

namespace Zenith.Core.Configuration;

internal interface IPersonalAppearanceStore
{
    bool Persistent { get; }
    bool Available(uint uid);
    string? Read(uint uid);
    void Replace(uint uid, string text);
}
internal sealed record PersonalAppearanceState(AppearanceSelection Saved, AppearanceSelection Active, bool Inherited, AppearanceState System, bool Persistent);

// The caller's UID is supplied only by trusted service entry points, never native data.
internal sealed class PersonalAppearanceService
{
    private sealed class Profile
    {
        public AppearanceSelection? Own;
        public AppearanceSelection Active = new(0, 0, 1);
        public ulong Revision = 1, Defaults;
    }
    private readonly AppearanceService _system;
    private readonly IPersonalAppearanceStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Profile> _profiles = new();
    public PersonalAppearanceService(AppearanceService system, IPersonalAppearanceStore store) { _system = system; _store = store; }
    public bool Available(uint uid) => _store.Available(uid);
    private Profile Load(uint uid, AppearanceState system)
    {
        if (_profiles.TryGetValue(uid, out var profile)) { return profile; }
        string? text = _store.Read(uid); (uint Mode, uint Accent, uint Materials, uint Translucency)? own = text is null || text.Trim() == "VERSION=1\nINHERIT=1" ? null : AppearanceService.Parse(text);
        profile = new Profile { Defaults = system.Saved.Revision };
        if (own.HasValue) { var v = own.Value; profile.Own = new(v.Mode, v.Accent, 1, v.Materials, v.Translucency); }
        profile.Active = system.Active with { Revision = 0 }; _profiles.Add(uid, profile); return profile;
    }
    public PersonalAppearanceState Snapshot(uint uid)
    {
        lock (_gate)
        {
            var system = _system.Snapshot();
            if (!Available(uid)) { return new(system.Saved, system.Active, true, system, false); }
            var p = Load(uid, system);
            if (p.Own is null && p.Defaults != system.Saved.Revision)
            { if (p.Revision == ulong.MaxValue) { throw new InvalidOperationException("personal revision exhausted"); } p.Revision++; p.Defaults = system.Saved.Revision; }
            return new((p.Own ?? system.Saved) with { Revision = p.Revision }, p.Active, p.Own is null, system, _store.Persistent);
        }
    }
    public PersonalAppearanceState ForDisplay(uint uid)
    {
        try { return Snapshot(uid); }
        catch { var system = _system.Snapshot(); return new(system.Saved, system.Active, true, system, false); }
    }
    public int Save(uint uid, uint mode, uint accent, uint materials, uint level, ulong revision, bool inherit = false)
    {
        if (!Available(uid)) { return -13; }
        if (mode > 1 || accent > 5 || materials > 7 || level > 100) { return -22; }
        lock (_gate)
        {
            try
            {
                var state = Snapshot(uid); var p = _profiles[uid];
                if (state.Saved.Revision != revision) { return -11; }
                if (p.Revision == ulong.MaxValue) { return -75; }
                // Re-read before replacement so malformed/future files are never overwritten.
                string? old = _store.Read(uid);
                if (old is not null && old.Trim() != "VERSION=1\nINHERIT=1") { _ = AppearanceService.Parse(old); }
                _store.Replace(uid, inherit ? "VERSION=1\nINHERIT=1\n" : AppearanceService.Serialize(mode, accent, materials, level));
                p.Own = inherit ? null : new(mode, accent, p.Revision + 1, materials, level);
                p.Defaults = state.System.Saved.Revision; p.Revision++; return 0;
            }
            catch { return -5; }
        }
    }
    public void MarkActive(uint uid, AppearanceSelection selection)
    { lock (_gate) { if (_profiles.TryGetValue(uid, out var p)) { p.Active = selection; } } }
}
