using System.Runtime.InteropServices;
using WorkBookmark.Core;

namespace WorkBookmark.Windows;

/// <summary>
/// Latches fresh key/button/wheel input without inspecting its payload. Hooks belong to a
/// dedicated message loop: Office COM calls and the application's UI must never hold up
/// the system-wide low-level input hook chain.
/// </summary>
public sealed class InputPressObserver : IDisposable
{
    private readonly Thread thread;
    private readonly ManualResetEventSlim started = new();
    private readonly Native.HookProc keyboardCallback, mouseCallback;
    private readonly Action? onInput;
    private readonly ResumeInputSignal? inputSignal;
    private Exception? startupError;
    private uint threadId;
    private int state;
    private const int InputObserved = 1, Disposed = 2;

    public bool HasNewInput => (Volatile.Read(ref state) & InputObserved) != 0;

    public InputPressObserver(Action? onInput = null, ResumeInputSignal? inputSignal = null)
    {
        this.onInput = onInput;
        this.inputSignal = inputSignal;
        keyboardCallback = Keyboard;
        mouseCallback = Mouse;
        thread = new Thread(Run) { IsBackground = true, Name = "WorkBookmark input observer" };
        thread.Start();
        started.Wait();
        if (startupError is not null)
        {
            thread.Join();
            started.Dispose();
            throw new BookmarkException(ResultCode.ContextChanged);
        }
    }

    private void Run()
    {
        nint keyboard = 0, mouse = 0;
        try
        {
            threadId = GetCurrentThreadId();
            // Ensure Dispose can post WM_QUIT as soon as startup completes.
            PeekMessage(out _, 0, 0, 0, 0);
            var module = Native.GetModuleHandle(null);
            keyboard = Native.SetWindowsHookEx(13, keyboardCallback, module, 0);
            mouse = Native.SetWindowsHookEx(14, mouseCallback, module, 0);
            if (keyboard == 0 || mouse == 0) throw new BookmarkException(ResultCode.ContextChanged);
        }
        catch (Exception error) { startupError = error; }
        finally { started.Set(); }

        try
        {
            if (startupError is null)
            {
                // GetMessage dispatches the hook callbacks even when there are no posted
                // messages. This thread never runs COM, UI actions, process kills or I/O.
                while (GetMessage(out var message, 0, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
        }
        finally
        {
            if (keyboard != 0) Native.UnhookWindowsHookEx(keyboard);
            if (mouse != 0) Native.UnhookWindowsHookEx(mouse);
            // An unexpectedly ended observer must fail closed for pending operations,
            // including workers that only observe the shared parent signal.
            ObserveInput();
            GC.KeepAlive(keyboardCallback);
            GC.KeepAlive(mouseCallback);
        }
    }

    private nint Keyboard(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x0100 or 0x0104) ObserveInput();
        return Native.CallNextHookEx(0, code, message, data);
    }

    private nint Mouse(int code, nint message, nint data)
    {
        if (code >= 0 && message is 0x0201 or 0x0204 or 0x0207 or 0x020A or 0x020B or 0x020E) ObserveInput();
        return Native.CallNextHookEx(0, code, message, data);
    }

    private void ObserveInput()
    {
        if (Interlocked.CompareExchange(ref state, InputObserved, 0) != 0) return;
        // The cross-process latch is updated before any asynchronously dispatched action,
        // so the worker cannot restore a stale selection during UI/startup delays.
        inputSignal?.MarkInput();
        if (onInput is not null)
            ThreadPool.QueueUserWorkItem(static observer =>
            {
                if ((Volatile.Read(ref observer.state) & Disposed) == 0) observer.onInput!();
            }, this, preferLocal: false);
    }

    public void Dispose()
    {
        if ((Interlocked.Or(ref state, Disposed) & Disposed) != 0) return;
        PostThreadMessage(threadId, 0x0012, 0, 0);
        thread.Join();
        started.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal int X, Y;
        internal uint Private;
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message message, nint window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message message, nint window, uint minimum, uint maximum);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] private static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
}
