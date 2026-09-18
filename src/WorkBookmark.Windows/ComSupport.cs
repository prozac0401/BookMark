using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace WorkBookmark.Windows;

internal sealed class ComScope : IDisposable
{
    private readonly List<object> references = [];
    private readonly HashSet<object> seen = new(ReferenceEqualityComparer.Instance);
    internal object Keep(object value) { if (Marshal.IsComObject(value) && seen.Add(value)) references.Add(value); return value; }
    internal object Get(object instance, string name, params object?[] args) => Invoke(instance, name, BindingFlags.GetProperty, args);
    internal object Call(object instance, string name, params object?[] args) => Invoke(instance, name, BindingFlags.InvokeMethod, args);
    private object Invoke(object instance, string name, BindingFlags flags, object?[] args)
    {
        var value = instance.GetType().InvokeMember(name, flags | BindingFlags.OptionalParamBinding, null, instance, args, CultureInfo.GetCultureInfo("en-US"));
        return value is null ? null! : Keep(value);
    }
    internal string Text(object instance, string name, params object?[] args) => Convert.ToString(Get(instance, name, args), CultureInfo.InvariantCulture) ?? "";
    internal int Number(object instance, string name, params object?[] args) => Convert.ToInt32(Get(instance, name, args), CultureInfo.InvariantCulture);
    internal bool Flag(object instance, string name, params object?[] args) => Convert.ToBoolean(Get(instance, name, args), CultureInfo.InvariantCulture);
    internal static nint Identity(object value) { var id = Marshal.GetIUnknownForObject(value); Marshal.Release(id); return id; }
    internal static bool Same(object a, object b) => Identity(a) == Identity(b);
    public void Dispose()
    {
        for (var i = references.Count - 1; i >= 0; i--) { try { Marshal.FinalReleaseComObject(references[i]); } catch (InvalidComObjectException) { } }
        references.Clear(); seen.Clear();
    }
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IComServiceProvider
{
    [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
}

[ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellBrowser
{
    [PreserveSig] int GetWindow(out nint hwnd);
    [PreserveSig] int ContextSensitiveHelp(bool enter);
    [PreserveSig] int InsertMenusSB(nint shared, nint widths);
    [PreserveSig] int SetMenuSB(nint shared, nint oleMenu, nint active);
    [PreserveSig] int RemoveMenusSB(nint shared);
    [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
    [PreserveSig] int EnableModelessSB(bool enabled);
    [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
    [PreserveSig] int BrowseObject(nint pidl, uint flags);
    [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
    [PreserveSig] int GetControlWindow(uint id, out nint hwnd);
    [PreserveSig] int SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
    [PreserveSig] int QueryActiveShellView(out IShellView view);
    [PreserveSig] int OnViewWindowActive(IShellView view);
    [PreserveSig] int SetToolbarItems(nint buttons, uint count, uint flags);
}

[ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellView
{
    [PreserveSig] int GetWindow(out nint hwnd);
    [PreserveSig] int ContextSensitiveHelp(bool enter);
    [PreserveSig] int TranslateAccelerator(nint message);
    [PreserveSig] int EnableModeless(bool enable);
    [PreserveSig] int UIActivate(uint state);
    [PreserveSig] int Refresh();
    [PreserveSig] int CreateViewWindow(nint previous, nint settings, nint browser, nint rectangle, out nint hwnd);
    [PreserveSig] int DestroyViewWindow();
    [PreserveSig] int GetCurrentInfo(nint settings);
    [PreserveSig] int AddPropertySheetPages(uint reserved, nint callback, nint lParam);
    [PreserveSig] int SaveViewState();
    [PreserveSig] int SelectItem(nint pidl, uint flags);
    [PreserveSig] int GetItemObject(uint item, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object result);
}
