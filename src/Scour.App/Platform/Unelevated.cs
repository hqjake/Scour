using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Scour.App.Platform;

/// <summary>
/// Starts programs as the signed-in user instead of as Scour's administrator. A browser started from an
/// elevated process can't join the copy already running ("profile in use") and opens with admin rights.
/// Explorer's own desktop window is asked to start it, the way Windows itself would.
/// </summary>
public static class Unelevated
{
    /// <summary>Starts the program through Explorer; falls back to a normal start if Explorer isn't available.</summary>
    public static void Start(string file, string arguments = "")
    {
        try
        {
            StartViaExplorer(file, arguments);
            return;
        }
        catch { /* no Explorer shell (or COM refused): start it ourselves */ }
        Process.Start(new ProcessStartInfo(file, arguments) { UseShellExecute = true });
    }

    private static void StartViaExplorer(string file, string arguments)
    {
        var shellWindows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!)!;
        try
        {
            object location = CsidlDesktop, root = Missing;
            var desktop = shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch);
            var serviceProvider = (IServiceProvider)desktop;
            var service = TopLevelBrowser;
            var iid = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(serviceProvider.QueryService(ref service, ref iid, out var browserObject));
            var browser = (IShellBrowser)browserObject;
            browser.QueryActiveShellView(out var view);
            var dispatch = IDispatchIid;
            view.GetItemObject(SvgioBackground, ref dispatch, out var folderView);
            dynamic application = ((dynamic)folderView).Application;
            application.ShellExecute(file, arguments, "", "open", 1);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shellWindows);
        }
    }

    private static readonly object Missing = System.Reflection.Missing.Value;
    private static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    private static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");
    private static readonly Guid IDispatchIid = new("00020400-0000-0000-C000-000000000046");
    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioBackground = 0;

    // Only the methods used are declared; _VtblGap skips the vtable slots before them.

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        void _VtblGap1_8();

        [return: MarshalAs(UnmanagedType.IDispatch)]
        object FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root,
                            int windowClass, out int hwnd, int options);
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void _VtblGap1_12();
        void QueryActiveShellView(out IShellView view);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void _VtblGap1_12();
        void GetItemObject(uint item, ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
}
