using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WorkBookmark.App;

public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private readonly Dictionary<bool, (int Id, Hotkey Key)> _registered = new();
    private int _nextId = 100;
    public event Action<bool>? Pressed;
    public bool CaptureRegistered => _registered.ContainsKey(true);
    public bool RecentRegistered => _registered.ContainsKey(false);
    public HotkeyWindow(Hotkey capture, Hotkey recent)
    {
        CreateHandle(new CreateParams { Caption = "WorkBookmark.Hotkeys", Parent = new IntPtr(-3) });
        RegisterInitial(true, capture);
        RegisterInitial(false, recent);
    }
    private void RegisterInitial(bool capture, Hotkey key)
    {
        int id = ++_nextId;
        if (key.IsValid && RegisterHotKey(Handle, id, key.Modifiers | 0x4000, (uint)key.VirtualKey)) _registered[capture] = (id, key);
    }
    // Reserve all replacements before releasing any existing registration.
    public bool TryUpdate(Hotkey capture, Hotkey recent, out string error)
    {
        error = "";
        if (!capture.IsValid || !recent.IsValid || capture == recent) { error = "서로 다른 Ctrl 또는 Alt 포함 단축키를 선택해 주세요."; return false; }
        var pending = new Dictionary<bool, (int Id, Hotkey Key)>();
        foreach (var item in new[] { (Capture: true, Key: capture), (Capture: false, Key: recent) })
        {
            if (_registered.TryGetValue(item.Capture, out var existing) && existing.Key == item.Key) continue;
            int id = ++_nextId;
            if (!RegisterHotKey(Handle, id, item.Key.Modifiers | 0x4000, (uint)item.Key.VirtualKey))
            {
                foreach (var added in pending.Values) UnregisterHotKey(Handle, added.Id);
                error = $"{item.Key}을(를) 등록하지 못했습니다. 기존 단축키는 유지됩니다. 다른 조합을 선택해 주세요.";
                return false;
            }
            pending[item.Capture] = (id, item.Key);
        }
        foreach (var item in pending)
        {
            if (_registered.TryGetValue(item.Key, out var existing)) UnregisterHotKey(Handle, existing.Id);
            _registered[item.Key] = item.Value;
        }
        return true;
    }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312)
        {
            foreach (var item in _registered)
                if (item.Value.Id == message.WParam.ToInt32()) { Pressed?.Invoke(item.Key); break; }
            return;
        }
        base.WndProc(ref message);
    }
    public void Dispose()
    {
        foreach (var item in _registered.Values) UnregisterHotKey(Handle, item.Id);
        _registered.Clear();
        DestroyHandle();
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
