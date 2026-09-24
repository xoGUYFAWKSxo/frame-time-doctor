using System.Runtime.InteropServices;

namespace FrameTimeDoctor.Cli;

/// <summary>
/// Waits for a global hotkey (works while a fullscreen game has focus) or Enter in the console,
/// whichever comes first. Default is Ctrl+Shift+F10, which no common game or overlay binds.
/// </summary>
static class Hotkey
{
    const uint ModControl = 0x2, ModShift = 0x4, ModNoRepeat = 0x4000, VkF10 = 0x79, WmHotkey = 0x312, WmQuit = 0x12;
    public const string Description = "Ctrl+Shift+F10";

    public static Task WaitAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        uint threadId = 0;

        var thread = new Thread(() =>
        {
            threadId = GetCurrentThreadId();
            if (!RegisterHotKey(IntPtr.Zero, 1, ModControl | ModShift | ModNoRepeat, VkF10))
            {
                Console.WriteLine($"  (Couldn't register {Description}; another app may own it. Use Enter instead.)");
                return;
            }
            try
            {
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                    if (msg.message == WmHotkey) { tcs.TrySetResult(); break; }
            }
            finally { UnregisterHotKey(IntPtr.Zero, 1); }
        }) { IsBackground = true };
        thread.Start();

        var console = Task.Run(() =>
        {
            while (!tcs.Task.IsCompleted && !ct.IsCancellationRequested)
            {
                if (!Console.IsInputRedirected && Console.KeyAvailable && Console.ReadKey(intercept: true).Key == ConsoleKey.Enter)
                    tcs.TrySetResult();
                Thread.Sleep(50);
            }
        }, ct);

        ct.Register(() => tcs.TrySetCanceled());
        tcs.Task.ContinueWith(_ => { if (threadId != 0) PostThreadMessage(threadId, WmQuit, IntPtr.Zero, IntPtr.Zero); });
        return tcs.Task;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Msg { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x; public int y; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] static extern int GetMessage(out Msg msg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
}
