using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace MacroPad
{
    /// <summary>Un programa que tiene una ventana abierta en este momento.</summary>
    public class RunningApp
    {
        public string Title { get; set; } = "";          // nombre del programa (ej.: "Discord")
        public string WindowTitle { get; set; } = "";    // título de su ventana (solo para buscar)
        public string ProcessName { get; set; } = "";    // ej.: "Discord"
        public string? FilePath { get; set; }            // ruta completa del .exe, si Windows deja leerla

        /// <summary>Lo que se guarda en la acción: la ruta exacta, o el nombre del .exe si no hay ruta.</summary>
        public string Value => FilePath ?? ProcessName + ".exe";
    }

    public static class RunningApps
    {
        /// <summary>Lista los programas con ventana visible (sin repetir y sin MacroPad).</summary>
        public static List<RunningApp> List()
        {
            var result = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);
            int myId = Environment.ProcessId;

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (process.Id == myId) continue;
                    if (process.MainWindowHandle == IntPtr.Zero) continue;

                    string windowTitle = process.MainWindowTitle;
                    if (string.IsNullOrWhiteSpace(windowTitle)) continue;

                    // Las apps de la Tienda aparecen bajo este proceso "anfitrión": no sirve para abrirlas
                    if (process.ProcessName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) continue;

                    string? path = null;
                    try { path = process.MainModule?.FileName; }
                    catch (Win32Exception) { /* programa protegido o de administrador */ }
                    catch (InvalidOperationException) { /* se cerró mientras lo revisábamos */ }

                    string title = process.ProcessName;
                    if (path != null)
                    {
                        try
                        {
                            string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                            if (!string.IsNullOrWhiteSpace(description)) title = description.Trim();
                        }
                        catch { /* nos quedamos con el nombre del proceso */ }
                    }

                    string key = path ?? process.ProcessName;
                    if (!result.ContainsKey(key))
                    {
                        result[key] = new RunningApp
                        {
                            Title = title,
                            WindowTitle = windowTitle,
                            ProcessName = process.ProcessName,
                            FilePath = path
                        };
                    }
                }
                catch { /* un proceso problemático no debe impedir listar los demás */ }
                finally { process.Dispose(); }
            }

            return result.Values
                .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    /// <summary>Comprueba que un programa existe y, si hay un error de escritura, propone el nombre correcto.</summary>
    public static class ProgramResolver
    {
        private static readonly string[] DefaultExtensions = { ".exe", ".cmd", ".bat", ".com", ".lnk" };
        private const string AppPathsKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";

        /// <summary>Devuelve null si todo está bien, o un mensaje que explica el problema.</summary>
        public static string? Check(string value, bool allowRunning)
        {
            string typed = value.Trim().Trim('"');

            if (TryResolve(typed)) return null;
            if (allowRunning && IsRunning(typed)) return null;

            bool hasFolder = typed.Contains('\\') || typed.Contains('/') || (typed.Length > 1 && typed[1] == ':');

            if (hasFolder)
                return $"No existe el archivo «{typed}». Usa los botones de abajo para elegir el programa sin escribir la ruta.";

            string message = $"No encuentro el programa «{typed}».";
            string? suggestion = Suggest(typed);
            if (suggestion != null)
                message += $" ¿Quisiste decir «{suggestion}»?";

            return message + " También puedes elegirlo con los botones de abajo.";
        }

        /// <summary>true si el nombre o la ruta corresponde a algo que Windows puede abrir.</summary>
        public static bool TryResolve(string value)
        {
            string v = value.Trim().Trim('"');
            if (v.Length == 0) return false;

            // Protocolos como ms-settings: o shell: (tienen ":" pero no son una unidad C:)
            if (v.Contains(':') && !(v.Length > 1 && v[1] == ':') && !v.Contains('\\') && !v.Contains('/'))
                return true;

            // Ruta completa (o con carpeta)
            if (v.Contains('\\') || v.Contains('/') || (v.Length > 1 && v[1] == ':'))
            {
                string expanded = Environment.ExpandEnvironmentVariables(v);
                return File.Exists(expanded) || Directory.Exists(expanded);
            }

            // Nombre suelto: carpetas del PATH y de Windows
            string[] extensions = Path.HasExtension(v) ? new[] { "" } : DefaultExtensions;

            var folders = new List<string>();
            string? pathVariable = Environment.GetEnvironmentVariable("PATH");
            if (pathVariable != null)
                folders.AddRange(pathVariable.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim().Trim('"')));
            folders.Add(Environment.SystemDirectory);
            folders.Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

            foreach (string folder in folders)
            {
                foreach (string extension in extensions)
                {
                    string fileName = v + extension;
                    try
                    {
                        if (File.Exists(Path.Combine(folder, fileName))) return true;

                        // Los alias de la Tienda (WindowsApps) no siempre responden a File.Exists
                        if (folder.EndsWith("WindowsApps", StringComparison.OrdinalIgnoreCase) && Directory.Exists(folder)
                            && Directory.EnumerateFiles(folder, fileName).Any())
                            return true;
                    }
                    catch { /* carpeta del PATH inválida o sin acceso */ }
                }
            }

            // Programas registrados en "App Paths" (Chrome, Word, etc.)
            string appName = Path.HasExtension(v) ? v : v + ".exe";
            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(AppPathsKey + "\\" + appName);
                    if (key != null) return true;
                }
                catch { }
            }

            return false;
        }

        private static bool IsRunning(string value)
        {
            string name = Path.GetFileNameWithoutExtension(value);
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return running;
        }

        // ----- Sugerencias para errores de escritura -----

        private static string? Suggest(string typed)
        {
            string target = Path.GetFileNameWithoutExtension(typed).ToLowerInvariant();
            if (target.Length < 3) return null;

            string? best = null;
            int bestDistance = int.MaxValue;
            int limit = Math.Max(2, target.Length / 3);

            foreach (string candidate in KnownNames())
            {
                string name = Path.GetFileNameWithoutExtension(candidate).ToLowerInvariant();
                int distance = Distance(target, name);

                if (distance < bestDistance && distance <= limit)
                {
                    bestDistance = distance;
                    best = candidate;
                }
            }

            return best;
        }

        // Nombres conocidos: programas en ejecución y programas registrados en Windows
        private static IEnumerable<string> KnownNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var process in Process.GetProcesses())
            {
                try { names.Add(process.ProcessName + ".exe"); }
                catch { }
                finally { process.Dispose(); }
            }

            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var key = root.OpenSubKey(AppPathsKey);
                    if (key == null) continue;
                    foreach (string sub in key.GetSubKeyNames()) names.Add(sub);
                }
                catch { }
            }

            return names;
        }

        // Distancia de Levenshtein: cuántas letras hay que cambiar, añadir o quitar para pasar de una palabra a otra
        private static int Distance(string a, string b)
        {
            var previous = new int[b.Length + 1];
            var current = new int[b.Length + 1];

            for (int j = 0; j <= b.Length; j++) previous[j] = j;

            for (int i = 1; i <= a.Length; i++)
            {
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                }
                (previous, current) = (current, previous);
            }

            return previous[b.Length];
        }
    }
}
