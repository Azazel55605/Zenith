using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Cosmos.Kernel.System.Graphics;
using Zenith.Core.Configuration;
using Zenith.Core.Security;
using MemoryInfo = Cosmos.Kernel.System.Diagnostics.MemoryInfo;
namespace Zenith.Gui.Shell;

/// <summary>BSP session-owner adapter for the pinned 3.0.89 canvases. Virtio and GOP
/// ignore Mode requests; their published static catalogues are not runtime capability.</summary>
internal static class DisplayController
{
    [DllImport("*", EntryPoint="zenith_svga_register")]
    [SuppressGCTransition] private static extern uint Register(uint index);
    [DllImport("*", EntryPoint="zenith_vmware_screen_size")]
    [SuppressGCTransition] private static extern uint HostScreenSize();
    private static Canvas s_physical=null!;
    private static DisplayLayout[] s_modes=Array.Empty<DisplayLayout>();
    private static DisplayLayout s_layout;
    private static string s_driver="";
    private static Desktop? s_desktop;
    private static Desktop.DisplayFrames? s_previous;
    private static Canvas? s_previousCanvas;
    private static DisplayLayout s_previousLayout;
    private static bool s_preferencesLoaded;
    private static DisplayLayout s_boot;
    /// <summary>VMware SVGA keeps a mode across a restart, while the firmware and bootloader draw for
    /// their own: put the boot mode back before restart or power-off.</summary>
    private static void RestoreBootMode()
    {
        if(s_physical is null || !s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase)
            || (s_physical.Width==s_boot.Width && s_physical.Height==s_boot.Height)) { return; }
        if (!DisplayLayout.FitsVmware(s_boot, Register(4), Register(5), Register(15), Register(14))) { return; }
        SetHardware(s_boot);
        Zenith.Core.Log.Write("display","restored boot mode "+s_boot.Width+"x"+s_boot.Height+" for firmware");
    }
    public static unsafe Canvas Initialize()
    {
        s_physical=Canvas.GetFullScreen(); s_driver=s_physical.Name;
        Zenith.Gui.Graphics.MaterialResources.Configure(MemoryInfo.TotalPages * 4096UL);
        s_layout=new(s_physical.Width,s_physical.Height,100); s_boot=s_layout;
        var firmware = Cosmos.Kernel.Boot.Limine.Limine.Framebuffer.Response;
        if (firmware != null && firmware->FramebufferCount > 0)
        {
            var fb = firmware->Framebuffers[0];
            if (fb->Width is >= 640 and <= 16384 && fb->Height is >= 480 and <= 16384)
            { s_boot = new((int)fb->Width, (int)fb->Height, 100); }
        }
        Zenith.Core.PowerControl.BeforeFirmware=RestoreBootMode;
        var modes=new List<DisplayLayout> {s_layout};
        // ApplyMode in the pinned VMware canvases changes hardware. Bound its
        // advertised list by the actual register limits and framebuffer memory.
        if(s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase))
        {
            uint maxW=Register(4),maxH=Register(5),vram=Register(15),offset=Register(14);
            // The pinned driver keeps a mutable per-canvas list behind this
            // read-only view, and validates SetMode against it. Extend only with
            // dimensions which the actual device and both VRAM planes support.
            if (s_physical.AvailableModes is List<Mode> catalogue)
            {
                foreach (var size in new[] { (2560,1440), (2560,1600), (3440,1440), (3840,2160), (3840,2400), (5120,1440), (5120,2880), (7680,4320) })
                {
                    var candidate = new DisplayLayout(size.Item1, size.Item2, 100);
                    var mode = new Mode(size.Item1, size.Item2, ColorDepth.ColorDepth32);
                    if (DisplayLayout.FitsVmware(candidate,maxW,maxH,vram,offset) && !catalogue.Contains(mode)) { catalogue.Add(mode); }
                }
            }
            foreach(var mode in s_physical.AvailableModes)
            {
                var m=new DisplayLayout(mode.Width,mode.Height,100);
                if(mode.ColorDepth!=ColorDepth.ColorDepth32 || !DisplayLayout.FitsVmware(m,maxW,maxH,vram,offset) || modes.Contains(m)) { continue; }
                if(modes.Count<24) { modes.Add(m); }
            }
        }
        s_modes=modes.ToArray();
        // The splash precedes VFS/settings initialization. Fit it now; applying
        // saved desktop preferences later must not be the first mode change.
        if (!SafeBoot() && AutomaticLayout() is DisplayLayout initial)
        { SetHardware(initial); s_layout = initial; SessionDisplay.Percent = initial.Percent; }
        return s_layout.Percent == 100 ? s_physical : new DisplayCanvas(s_physical, s_layout);
    }
    public static void LoadPreferences()
    {
        // Boot splash acquires the framebuffer before shared settings/VFS exist.
        if(s_preferencesLoaded || SystemConfiguration.Display is null) { return; }
        s_preferencesLoaded=true;
        DisplayLayout? saved=SystemConfiguration.Display.Saved();
        if(SafeBoot()) { saved=null; }
        // Nothing kept yet: fit the host's screen (VMware reports it) instead of the firmware's mode.
        else if(saved is null && AutomaticLayout() is DisplayLayout automatic) { saved=automatic; Zenith.Core.Log.Write("display","automatic "+automatic.Width+"x"+automatic.Height+" scale="+automatic.Scale); }
        // Boot has no old application state to preserve. Unsupported saved modes
        // fall back to firmware/current mode, retaining the file for diagnosis.
        if(saved is DisplayLayout wanted && wanted.Usable && HasMode(wanted))
        {
            ulong pages=(ulong)wanted.LogicalWidth*(ulong)wanted.LogicalHeight*16/4096+(ulong)wanted.Width*(ulong)wanted.Height*4/4096+8192;
            if(MemoryInfo.FreePages>=pages)
            {
                try
                {
                    Canvas screen=wanted.Percent==100?s_physical:new DisplayCanvas(s_physical,wanted);
                    SetHardware(wanted); s_layout=wanted;
                    SessionDisplay.ReplaceScreen(screen);
                }
                catch { SetHardware(s_layout); Zenith.Core.Log.Write("display","saved mode refused; using boot mode"); }
            }
            else { Zenith.Core.Log.Write("display","insufficient memory for saved mode; using boot mode"); }
        }
        SessionDisplay.Percent=s_layout.Percent;
        SystemConfiguration.Display.Attach(s_driver,s_layout,s_modes,null,null,Fractional);
        if(s_layout.Percent==100) { SessionDisplay.ReplaceScreen(s_physical); }
        RememberBootDisplay();
    }
    private static unsafe bool SafeBoot()
    {
        string line=Marshal.PtrToStringUTF8((IntPtr)Cosmos.Kernel.Boot.Limine.Limine.Cmdline)??"";
        foreach(string word in line.Split(' ')) { if(word=="display=safe") { return true; } }
        return false;
    }
    /// <summary>The largest supported mode within the host's screen, with an integer scale that keeps
    /// about 900 logical lines or more (4K gets 200%). Null off VMware or for implausible sizes.</summary>
    private static DisplayLayout? AutomaticLayout()
    {
        if(!s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase)) { return null; }
        uint size=HostScreenSize(); int w=(int)(size>>16),h=(int)(size&0xffff);
        if(size==0xffffffff || w<640 || h<480 || w>16384 || h>16384) { return null; }
        DisplayLayout best=default; bool found=false;
        foreach(var mode in s_modes)
        {
            if(mode.Width>w || mode.Height>h) { continue; }
            if(!found || (long)mode.Width*mode.Height>(long)best.Width*best.Height) { best=mode; found=true; }
        }
        if(!found) { return null; }
        int scale=Math.Clamp(best.Height/900,1,2);
        var layout=new DisplayLayout(best.Width,best.Height,scale*100);
        return layout.Usable?layout:new DisplayLayout(best.Width,best.Height,100);
    }
    private static bool HasMode(DisplayLayout layout)
    { foreach(var mode in s_modes) { if(mode.Width==layout.Width && mode.Height==layout.Height) { return true; } } return false; }
#if !ARCH_ARM64
    [DllImport("*", EntryPoint="zenith_svga_invalidate")]
    [SuppressGCTransition] private static extern void InvalidateDirect();
#else
    private static void InvalidateDirect() { }
#endif
    private static void SetHardware(DisplayLayout layout)
    {
        Zenith.Gui.Graphics.HardwareCursor.Reset();
        InvalidateDirect();
        if(s_physical.Width!=layout.Width || s_physical.Height!=layout.Height)
        { Canvas.GetFullScreen(new Mode(layout.Width,layout.Height,ColorDepth.ColorDepth32)); }
        if(s_physical.Width!=layout.Width || s_physical.Height!=layout.Height) { throw new InvalidOperationException("driver did not apply requested mode"); }
        if(s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase)
            && (Register(2)!=(uint)layout.Width || Register(3)!=(uint)layout.Height || Register(7)!=32
                || Register(12)!=(uint)layout.Width*4 || !DisplayLayout.FitsVmware(layout,Register(4),Register(5),Register(15),Register(14))))
        { throw new InvalidOperationException("SVGA hardware mode readback differs from request"); }
        InvalidateDirect();
    }
    public static void Attach(Desktop desktop)
    {
        s_desktop=desktop; SystemConfiguration.Display.Attach(s_driver,s_layout,s_modes,Apply,Finish,Fractional);
    }
    /// <summary>125/150/175% are presented filtered by the x64 VMware presenter only.</summary>
    private static bool Fractional => s_driver.StartsWith("VMWare",StringComparison.OrdinalIgnoreCase) && RuntimeInformation.ProcessArchitecture==Architecture.X64;
    private static bool Apply(DisplayLayout layout)
    {
        if(s_desktop is null || (!layout.Integer && !Fractional)) { return false; }
        if(s_previous is not null)
        {
            if(layout!=s_previousLayout) { return false; }
            SetHardware(layout); SessionDisplay.ReplaceScreen(s_previousCanvas!); SessionDisplay.Percent=layout.Percent;
            s_desktop.UseDisplay(s_previousCanvas!,s_previous); s_layout=layout; SessionDisplay.ConfigurePointer();
            SessionDisplay.LiveFrame=s_desktop.FramePixels; return true;
        }
        // Retain every old full-screen buffer until Keep/Revert. Allocate first,
        // including scaled output, and refuse before touching the device if short.
        ulong pages=(ulong)layout.LogicalWidth*(ulong)layout.LogicalHeight*4*4/4096
            +(layout.Percent==100?0:(ulong)layout.Width*(ulong)layout.Height*4/4096)+8192;
        if(MemoryInfo.FreePages<pages) { return false; }
        Desktop.DisplayFrames frames,old;
        Canvas screen,oldCanvas;
        var oldLayout=s_layout;
        try
        {
            frames=s_desktop.PrepareDisplay(layout.LogicalWidth,layout.LogicalHeight);
            screen=layout.Percent==100?s_physical:new DisplayCanvas(s_physical,layout);
            old=s_desktop.CaptureDisplay(); oldCanvas=SessionDisplay.Screen;
        }
        catch { return false; } // No device or desktop mutation has occurred.
        try
        {
            SetHardware(layout);
            SessionDisplay.ReplaceScreen(screen); SessionDisplay.Percent=layout.Percent;
            s_desktop.UseDisplay(screen,frames); SessionDisplay.ConfigurePointer();
        }
        catch
        {
            SetHardware(oldLayout); SessionDisplay.ReplaceScreen(oldCanvas); SessionDisplay.Percent=oldLayout.Percent;
            s_desktop.UseDisplay(oldCanvas,old); SessionDisplay.ConfigurePointer(); frames.Bar.Dispose();
            SessionDisplay.LiveFrame=s_desktop.FramePixels; return false;
        }
        s_previous=old; s_previousCanvas=oldCanvas; s_previousLayout=oldLayout; s_layout=layout;
        SessionDisplay.LiveFrame=s_desktop.FramePixels;
        Zenith.Core.Log.Write("display","preview "+layout.Width+"x"+layout.Height+" scale="+layout.Scale+(layout.Integer?"":" percent="+layout.Percent)); return true;
    }
    private static void Finish(bool kept)
    { if(kept) { s_previous?.Bar.Dispose(); RememberBootDisplay(); } s_previous=null; s_previousCanvas=null; Zenith.Core.Log.Write("display",kept?"kept":"restored"); }
    private static void RememberBootDisplay()
    {
        try { Zenith.Core.Boot.NativeKernelUpdates.SaveDisplay(s_layout.Width, s_layout.Height); }
        catch (Exception e) { Zenith.Core.Log.Write("display", "boot menu preference not saved: " + e.Message); }
    }
    public static void Tick() => SystemConfiguration.Display.Tick(Zenith.Core.Configuration.DisplayService.Now);
    public static void Detach()
    { SystemConfiguration.Display.Detach(Zenith.Core.Configuration.DisplayService.Now); s_desktop=null; }
}
