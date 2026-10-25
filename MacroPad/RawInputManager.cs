using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MacroPad
{
    public class RawKeyEventArgs : EventArgs
    {
        public string DeviceName { get; set; } = "";
        public ushort VKey { get; set; }
        public bool IsKeyDown { get; set; }
    }

    public class KeyboardInfo
    {
        public IntPtr Handle { get; set; }
        public string Name { get; set; } = "";
    }

    public class RawInputManager
    {
        // ---------- Constantes de Windows ----------
        private const int WM_INPUT = 0x00FF;
        private const uint RID_INPUT = 0x10000003;
        private const uint RIDI_DEVICENAME = 0x20000007;
        private const uint RIM_TYPEKEYBOARD = 1;
        private const uint RIDEV_INPUTSINK = 0x00000100; // recibe teclas aunque la ventana no tenga foco
        private const ushort RI_KEY_BREAK = 0x01;        // bit que indica "tecla soltada"

        // ---------- Estructuras ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint dwType;
            public uint dwSize;
            public IntPtr hDevice;
            public IntPtr wParam;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode;
            public ushort Flags;
            public ushort Reserved;
            public ushort VKey;
            public uint Message;
            public uint ExtraInformation;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICELIST
        {
            public IntPtr hDevice;
            public uint dwType;
        }

        // ---------- Funciones de user32.dll ----------
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(
            RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(
            IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputDeviceList(
            IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetRawInputDeviceInfo(
            IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

        // ---------- Estado ----------
        private readonly Dictionary<IntPtr, string> _nameCache = new();

        public event EventHandler<RawKeyEventArgs>? KeyPressed;

        /// <summary>Registra la ventana para recibir eventos de TODOS los teclados.</summary>
        public void Register(IntPtr hwnd)
        {
            var device = new RAWINPUTDEVICE
            {
                usUsagePage = 0x01, // Generic Desktop
                usUsage = 0x06,     // Keyboard
                dwFlags = RIDEV_INPUTSINK,
                hwndTarget = hwnd
            };

            bool ok = RegisterRawInputDevices(
                new[] { device }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());

            if (!ok)
                throw new InvalidOperationException(
                    "No se pudo registrar Raw Input. Error: " + Marshal.GetLastWin32Error());
        }

        /// <summary>Hook para HwndSource.AddHook: intercepta el mensaje WM_INPUT.</summary>
        public IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_INPUT)
                ProcessRawInput(lParam);

            return IntPtr.Zero;
        }

        private void ProcessRawInput(IntPtr hRawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
            uint size = 0;

            // 1) Preguntar cuántos bytes ocupa el evento
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0) return;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                // 2) Leer el evento
                if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size)
                    return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                if (header.dwType != RIM_TYPEKEYBOARD) return;

                var kb = Marshal.PtrToStructure<RAWKEYBOARD>(IntPtr.Add(buffer, (int)headerSize));

                KeyPressed?.Invoke(this, new RawKeyEventArgs
                {
                    DeviceName = GetDeviceName(header.hDevice),
                    VKey = kb.VKey,
                    IsKeyDown = (kb.Flags & RI_KEY_BREAK) == 0
                });
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>Devuelve el identificador de hardware (ruta HID) de un dispositivo.</summary>
        private string GetDeviceName(IntPtr hDevice)
        {
            if (_nameCache.TryGetValue(hDevice, out var cached))
                return cached;

            uint chars = 0;
            GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);

            string name = "(desconocido)";
            if (chars > 0)
            {
                IntPtr ptr = Marshal.AllocHGlobal((int)chars * 2);
                try
                {
                    if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, ptr, ref chars) > 0)
                        name = Marshal.PtrToStringUni(ptr) ?? name;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }

            _nameCache[hDevice] = name;
            return name;
        }

        /// <summary>Lista los teclados conectados en este momento.</summary>
        public List<KeyboardInfo> GetKeyboards()
        {
            var result = new List<KeyboardInfo>();
            uint entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
            uint count = 0;

            GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
            if (count == 0) return result;

            IntPtr list = Marshal.AllocHGlobal((int)(entrySize * count));
            try
            {
                GetRawInputDeviceList(list, ref count, entrySize);

                for (int i = 0; i < count; i++)
                {
                    var item = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(
                        IntPtr.Add(list, i * (int)entrySize));

                    if (item.dwType == RIM_TYPEKEYBOARD)
                        result.Add(new KeyboardInfo
                        {
                            Handle = item.hDevice,
                            Name = GetDeviceName(item.hDevice)
                        });
                }
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }

            return result;
        }
    }
}
