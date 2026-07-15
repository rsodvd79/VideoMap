using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace VideoMap.App;

static class MacDockIcon
{
    [DllImport("libobjc.dylib", EntryPoint = "objc_getClass")]
    static extern IntPtr ObjcGetClass([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc.dylib", EntryPoint = "sel_registerName")]
    static extern IntPtr SelRegisterName([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr Send(IntPtr receiver, IntPtr sel);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendPtr(IntPtr receiver, IntPtr sel, IntPtr a1);

    [DllImport("libobjc.dylib", EntryPoint = "objc_msgSend")]
    static extern IntPtr SendPtrNint(IntPtr receiver, IntPtr sel, IntPtr a1, nint a2);

    public static void Apply()
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            var uri = new Uri("avares://VideoMap.App/Assets/AppIcon.png");
            using var stream = AssetLoader.Open(uri);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();

            var NSData = ObjcGetClass("NSData");
            var NSImage = ObjcGetClass("NSImage");
            var NSApp = ObjcGetClass("NSApplication");

            var selShared = SelRegisterName("sharedApplication");
            var selDataBytes = SelRegisterName("dataWithBytes:length:");
            var selAlloc = SelRegisterName("alloc");
            var selInitData = SelRegisterName("initWithData:");
            var selSetIcon = SelRegisterName("setApplicationIconImage:");

            var app = Send(NSApp, selShared);

            var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            IntPtr nsData;
            try { nsData = SendPtrNint(NSData, selDataBytes, pin.AddrOfPinnedObject(), bytes.Length); }
            finally { pin.Free(); }

            var nsImageAlloc = Send(NSImage, selAlloc);
            var nsImage = SendPtr(nsImageAlloc, selInitData, nsData);

            SendPtr(app, selSetIcon, nsImage);
        }
        catch
        {
        }
    }
}
