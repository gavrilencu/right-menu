using System;
using System.Runtime.InteropServices;

namespace RadialMenu
{
    /// <summary>
    /// Global low-level mouse + keyboard hooks. Handlers run on the thread that installed
    /// the hooks (the UI thread) and must return quickly: Windows silently drops hooks
    /// that exceed LowLevelHooksTimeout. Return true from a handler to swallow the event.
    /// </summary>
    public sealed class Hooks : IDisposable
    {
        public delegate bool MouseHandler(int message, ref MSLLHOOKSTRUCT data);
        public delegate bool KeyHandler(int message, ref KBDLLHOOKSTRUCT data);

        // Kept in fields so the GC never collects the delegates while Windows holds them.
        readonly Native.HookProc mouseProc;
        readonly Native.HookProc keyProc;
        readonly MouseHandler onMouse;
        readonly KeyHandler onKey;
        IntPtr mouseHook = IntPtr.Zero;
        IntPtr keyHook = IntPtr.Zero;

        public Hooks(MouseHandler onMouse, KeyHandler onKey)
        {
            this.onMouse = onMouse;
            this.onKey = onKey;
            mouseProc = MouseCallback;
            keyProc = KeyCallback;
        }

        public void Install()
        {
            Uninstall();
            IntPtr module = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, mouseProc, module, 0);
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, keyProc, module, 0);
            if (mouseHook == IntPtr.Zero)
                throw new InvalidOperationException("SetWindowsHookEx(WH_MOUSE_LL) a eșuat: " + Marshal.GetLastWin32Error());
        }

        public void Uninstall()
        {
            if (mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        }

        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                bool swallow = false;
                try { swallow = onMouse((int)wParam, ref data); }
                catch (Exception ex) { ErrorLog.Write(ex); }
                if (swallow) return new IntPtr(1);
            }
            return Native.CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));
                bool swallow = false;
                try { swallow = onKey((int)wParam, ref data); }
                catch (Exception ex) { ErrorLog.Write(ex); }
                if (swallow) return new IntPtr(1);
            }
            return Native.CallNextHookEx(keyHook, nCode, wParam, lParam);
        }

        public void Dispose() { Uninstall(); }
    }
}
