using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Input;

namespace MacroPad
{
    public enum ActionType
    {
        OpenProgram,   // Value = "calc.exe" o ruta completa
        OpenUrl,       // Value = "https://..."
        SendShortcut,  // Value = "Ctrl+C", "Win+D", "Ctrl+Shift+Escape"
        TypeText,      // Value = texto a escribir
        FocusProgram,  // Value = "discord.exe" o ruta completa: si ya tiene ventana abierta la trae al frente; si no, lo abre
        Sequence       // Value = guion de varios pasos, uno por línea (ver ParseSequence)
    }

    public class MacroAction
    {
        public string Name { get; set; } = "";
        public ActionType Type { get; set; }
        public string Value { get; set; } = "";
    }

    /// <summary>Un paso de una macro: una acción o una pausa.</summary>
    public class MacroStep
    {
        public ActionType? Type { get; set; }   // null = pausa
        public string Value { get; set; } = "";
        public int WaitMs { get; set; }
        public int Line { get; set; }
    }

    public static class ActionEngine
    {
        // ---------- SendInput (user32.dll) ----------
        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;   // necesario para que el tamaño de INPUT sea correcto
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        // ---------- API pública ----------
        public static void Execute(MacroAction action)
        {
            switch (action.Type)
            {
                case ActionType.OpenProgram:
                case ActionType.OpenUrl:
                    Process.Start(new ProcessStartInfo(action.Value) { UseShellExecute = true });
                    break;

                case ActionType.FocusProgram:
                    OpenOrFocus(action.Value);
                    break;

                case ActionType.Sequence:
                    RunSequence(action.Value);
                    break;

                case ActionType.SendShortcut:
                    SendShortcut(action.Value);
                    break;

                case ActionType.TypeText:
                    TypeText(action.Value);
                    break;
            }
        }

        // ---------- Macros de varios pasos ----------

        private static int _sequenceRunning;

        /// <summary>
        /// Convierte el guion de una macro en pasos. Una línea por paso:
        ///   abrir: calc.exe      enfocar: discord.exe     url: https://...
        ///   atajo: Ctrl+S        escribir: texto          esperar: 1500 (ms), 500ms o 2s
        /// Las líneas vacías o que empiezan con # se ignoran. Si hay un error, devuelve su descripción en "error".
        /// </summary>
        public static List<MacroStep> ParseSequence(string script, out string? error)
        {
            error = null;
            var steps = new List<MacroStep>();
            string[] lines = script.Replace("\r\n", "\n").Split('\n');

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                int number = i + 1;

                if (line.Length == 0 || line.StartsWith('#')) continue;

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    error = $"Línea {number}: usa el formato «palabra: valor» (por ejemplo, abrir: calc.exe).";
                    return steps;
                }

                string keyword = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();

                if (value.Length == 0)
                {
                    error = $"Línea {number}: el paso «{keyword}» no tiene valor.";
                    return steps;
                }

                var step = new MacroStep { Value = value, Line = number };

                switch (keyword)
                {
                    case "abrir":    step.Type = ActionType.OpenProgram;  break;
                    case "enfocar":  step.Type = ActionType.FocusProgram; break;
                    case "url":      step.Type = ActionType.OpenUrl;      break;
                    case "escribir": step.Type = ActionType.TypeText;     break;

                    case "atajo":
                        step.Type = ActionType.SendShortcut;
                        try
                        {
                            foreach (string part in value.Split('+')) ParseKey(part);
                        }
                        catch (ArgumentException ex)
                        {
                            error = $"Línea {number}: {ex.Message}";
                            return steps;
                        }
                        break;

                    case "esperar":
                        if (!TryParseWait(value, out int ms))
                        {
                            error = $"Línea {number}: la pausa debe ser un tiempo entre 1 ms y 60 s (ej.: 500, 500ms, 1.5s).";
                            return steps;
                        }
                        step.WaitMs = ms;
                        break;

                    default:
                        error = $"Línea {number}: no entiendo «{keyword}». Usa abrir, enfocar, url, atajo, escribir o esperar.";
                        return steps;
                }

                steps.Add(step);
            }

            if (steps.Count == 0)
                error = "La macro no tiene ningún paso.";

            return steps;
        }

        // Acepta "1500" (milisegundos), "500ms", "2s" o "1.5s"
        private static bool TryParseWait(string text, out int ms)
        {
            ms = 0;
            var match = Regex.Match(text.Trim().ToLowerInvariant(), @"^(\d+(?:[.,]\d+)?)\s*(ms|s)?$");
            if (!match.Success) return false;

            double amount = double.Parse(match.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (match.Groups[2].Value == "s") amount *= 1000;

            if (amount < 1 || amount > 60000) return false;

            ms = (int)amount;
            return true;
        }

        private static void RunSequence(string script)
        {
            var steps = ParseSequence(script, out var error);
            if (error != null)
                throw new InvalidOperationException(error);

            // Evita que dos macros de varios pasos se mezclen si presionas otra tecla mientras una está corriendo
            if (Interlocked.CompareExchange(ref _sequenceRunning, 1, 0) != 0)
                throw new InvalidOperationException("Ya hay una macro de varios pasos en ejecución.");

            try
            {
                foreach (var step in steps)
                {
                    try
                    {
                        if (step.Type == null)
                            Thread.Sleep(step.WaitMs);
                        else
                            Execute(new MacroAction { Type = step.Type.Value, Value = step.Value });
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Paso de la línea {step.Line}: {ex.Message}", ex);
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _sequenceRunning, 0);
            }
        }

        // ---------- Abrir o enfocar un programa ----------

        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hWnd, bool fAltTab);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

        private static void OpenOrFocus(string value)
        {
            // "C:\...\Discord.exe", "discord.exe" o "discord" -> nombre de proceso "Discord"
            string processName = Path.GetFileNameWithoutExtension(value.Trim().Trim('"'));
            IntPtr window = IntPtr.Zero;

            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    // MainWindowHandle es 0 si el programa no tiene ventana visible (por ejemplo, oculto en la bandeja)
                    if (window == IntPtr.Zero && process.MainWindowHandle != IntPtr.Zero)
                        window = process.MainWindowHandle;
                }
                catch { /* el proceso pudo cerrarse mientras lo revisábamos */ }
                finally { process.Dispose(); }
            }

            if (window == IntPtr.Zero)
            {
                // No hay ventana que enfocar: se abre como siempre
                Process.Start(new ProcessStartInfo(value) { UseShellExecute = true });
                return;
            }

            BringToFront(window);
        }

        private static void BringToFront(IntPtr window)
        {
            // Windows limita qué programas pueden pasar al frente; nos "unimos" al hilo de la ventana activa para poder hacerlo
            IntPtr foreground = GetForegroundWindow();
            uint foregroundThread = foreground != IntPtr.Zero ? GetWindowThreadProcessId(foreground, out _) : 0;
            uint myThread = GetCurrentThreadId();
            bool attached = foregroundThread != 0 && foregroundThread != myThread
                            && AttachThreadInput(myThread, foregroundThread, true);

            try
            {
                if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
                BringWindowToTop(window);
                SetForegroundWindow(window);
            }
            finally
            {
                if (attached) AttachThreadInput(myThread, foregroundThread, false);
            }

            // Último recurso si Windows no hizo caso
            if (GetForegroundWindow() != window)
                SwitchToThisWindow(window, true);
        }

        // ---------- Atajos de teclado ----------
        private static void SendShortcut(string shortcut)
        {
            ushort[] keys = shortcut.Split('+').Select(ParseKey).ToArray();

            var inputs = new List<INPUT>();
            foreach (ushort vk in keys)                 // presionar en orden
                inputs.Add(VirtualKeyInput(vk, 0));
            foreach (ushort vk in keys.Reverse())       // soltar en orden inverso
                inputs.Add(VirtualKeyInput(vk, KEYEVENTF_KEYUP));

            Send(inputs);
        }

        private static ushort ParseKey(string token)
        {
            string t = token.Trim();

            switch (t.ToLowerInvariant())
            {
                case "ctrl":
                case "control": return 0x11;
                case "shift":   return 0x10;
                case "alt":     return 0x12;
                case "win":     return 0x5B;
                case "esc":     return 0x1B;
            }

            // Para el resto usamos los nombres de System.Windows.Input.Key (A, D, F5, Enter, Tab...)
            if (Enum.TryParse<Key>(t, true, out var key))
                return (ushort)KeyInterop.VirtualKeyFromKey(key);

            throw new ArgumentException("Tecla no reconocida: " + token);
        }

        // ---------- Escribir texto ----------
        private static void TypeText(string text)
        {
            var inputs = new List<INPUT>();
            foreach (char c in text)
            {
                inputs.Add(UnicodeInput(c, 0));
                inputs.Add(UnicodeInput(c, KEYEVENTF_KEYUP));
            }
            Send(inputs);
        }

        // ---------- Utilidades ----------
        private static INPUT VirtualKeyInput(ushort vk, uint flags) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } }
        };

        private static INPUT UnicodeInput(char c, uint extraFlags) => new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | extraFlags } }
        };

        private static void Send(List<INPUT> inputs)
        {
            if (inputs.Count == 0) return;

            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
            if (sent != inputs.Count)
                throw new InvalidOperationException("SendInput falló. Error: " + Marshal.GetLastWin32Error());
        }
    }
}
