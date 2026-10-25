using System;
using Microsoft.Win32;

namespace MacroPad
{
    /// <summary>
    /// Inicio automático con Windows usando HKCU\...\Run (no requiere permisos de administrador).
    /// El programa arranca con el argumento --minimized para quedarse en la bandeja.
    /// </summary>
    public static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MacroPad";

        public static bool IsEnabled()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) != null;
        }

        public static void SetEnabled(bool enabled)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

            if (enabled)
            {
                string exe = Environment.ProcessPath
                             ?? throw new InvalidOperationException("No se pudo obtener la ruta del programa.");
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
