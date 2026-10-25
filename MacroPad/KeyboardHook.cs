using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MacroPad
{
    public class HookKeyEventArgs
    {
        public int VKey { get; set; }
        public bool IsDown { get; set; }
        public bool IsExtended { get; set; }   // true en Enter, "/" y otras teclas del bloque numérico "extendidas"
    }

    /// <summary>
    /// Hook global de teclado. KeyEvent debe devolver true para BLOQUEAR la tecla
    /// (Windows no la entregará a ninguna aplicación).
    /// El manejador debe ser rápido: si tarda, Windows desactiva el hook.
    /// </summary>
    public class KeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_SYSKEYDOWN = 0x0104;

        private const uint LLKHF_EXTENDED = 0x01;
        private const uint LLKHF_LOWER_IL_INJECTED = 0x02;
        private const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        private readonly LowLevelKeyboardProc _proc;   // se guarda en un campo para que el recolector de basura no lo elimine
        private IntPtr _hookId = IntPtr.Zero;

        public Func<HookKeyEventArgs, bool>? KeyEvent { get; set; }

        public KeyboardHook()
        {
            _proc = HookCallback;
        }

        public void Start()
        {
            if (_hookId != IntPtr.Zero) return;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);

            if (_hookId == IntPtr.Zero)
                throw new InvalidOperationException(
                    "No se pudo instalar el hook de teclado. Error: " + Marshal.GetLastWin32Error());
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && KeyEvent != null)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Las teclas que enviamos nosotros con SendInput vienen marcadas como "inyectadas": se dejan pasar
                bool injected = (data.flags & (LLKHF_INJECTED | LLKHF_LOWER_IL_INJECTED)) != 0;

                if (!injected)
                {
                    int msg = wParam.ToInt32();
                    var args = new HookKeyEventArgs
                    {
                        VKey = (int)data.vkCode,
                        IsDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN,
                        IsExtended = (data.flags & LLKHF_EXTENDED) != 0
                    };

                    bool block;
                    try { block = KeyEvent(args); }
                    catch { block = false; }   // ante cualquier error, nunca bloqueamos el teclado

                    if (block) return (IntPtr)1;
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}
