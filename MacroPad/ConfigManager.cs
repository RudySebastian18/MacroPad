using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace MacroPad
{
    // ============ Formato del archivo config.json ============

    /// <summary>Un perfil tal como se guarda en el archivo.</summary>
    public class ProfileFile
    {
        public string Name { get; set; } = "";
        public List<string> Apps { get; set; } = new();                       // programas que activan el perfil (ej.: "excel")
        public Dictionary<string, MacroAction> Bindings { get; set; } = new(); // tecla -> acción
    }

    public class AppConfig
    {
        public List<ProfileFile> Profiles { get; set; } = new();
        public string? PinnedProfile { get; set; }   // perfil fijado a mano; null = cambio automático

        // Formato antiguo (sin perfiles): solo se lee para migrarlo, nunca se escribe
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, MacroAction>? Bindings { get; set; }
    }

    // ============ Datos en memoria ============

    public class ProfileData
    {
        public string Name { get; set; } = ConfigManager.GeneralName;
        public List<string> Apps { get; set; } = new();
        public Dictionary<Key, MacroAction> Bindings { get; set; } = new();
    }

    public class LoadedConfig
    {
        public List<ProfileData> Profiles { get; set; } = new();
        public string? PinnedProfile { get; set; }
        public bool Migrated { get; set; }

        /// <summary>El perfil "General" siempre existe y es el primero.</summary>
        public ProfileData General => Profiles[0];
    }

    public static class ConfigManager
    {
        public const string GeneralName = "General";

        public static string ConfigFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MacroPad");

        public static string ConfigPath => Path.Combine(ConfigFolder, "config.json");

        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            Converters = { new JsonStringEnumConverter() }   // guarda "OpenProgram" en vez de 0
        };

        /// <summary>Configuración que se crea la primera vez que se ejecuta el programa.</summary>
        public static LoadedConfig Defaults()
        {
            var general = new ProfileData { Name = GeneralName };
            general.Bindings[Key.NumPad1] = new MacroAction { Name = "Calculadora",             Type = ActionType.OpenProgram,  Value = "calc.exe" };
            general.Bindings[Key.NumPad2] = new MacroAction { Name = "Abrir Google",            Type = ActionType.OpenUrl,      Value = "https://www.google.com" };
            general.Bindings[Key.NumPad3] = new MacroAction { Name = "Administrador de tareas", Type = ActionType.SendShortcut, Value = "Ctrl+Shift+Esc" };
            general.Bindings[Key.NumPad4] = new MacroAction { Name = "Escribir saludo",         Type = ActionType.TypeText,     Value = "Hola desde MacroPad" };
            general.Bindings[Key.NumPad5] = new MacroAction { Name = "Bloc de notas",           Type = ActionType.OpenProgram,  Value = "notepad.exe" };

            return new LoadedConfig { Profiles = { general } };
        }

        /// <summary>
        /// Carga la configuración. Si el archivo no existe, lo crea con los valores por defecto.
        /// Si tiene el formato antiguo (sin perfiles), lo convierte a un perfil "General" y guarda una copia (.bak).
        /// Si el archivo tiene errores, usa los valores por defecto SIN sobrescribir tu archivo.
        /// </summary>
        public static LoadedConfig Load(List<string> warnings)
        {
            try
            {
                if (!File.Exists(ConfigPath))
                {
                    var defaults = Defaults();
                    Save(defaults);
                    return defaults;
                }

                var config = Parse(File.ReadAllText(ConfigPath), warnings);
                if (config == null)
                {
                    warnings.Add("config.json está vacío o no tiene perfiles ni teclas. Se usan las acciones por defecto; tu archivo no se modificó.");
                    return Defaults();
                }

                if (config.Migrated)
                {
                    try { Save(config); }   // crea config.json.bak con el archivo anterior
                    catch (Exception ex) { warnings.Add("No se pudo actualizar el formato de config.json: " + ex.Message); }
                }

                return config;
            }
            catch (Exception ex)
            {
                warnings.Add("No se pudo leer config.json (" + ex.Message + "). Se usan las acciones por defecto; tu archivo no se modificó.");
                return Defaults();
            }
        }

        /// <summary>
        /// Lee un archivo de configuración de otro lugar (para importarlo). No modifica nada:
        /// devuelve null y un mensaje en "error" si el archivo no sirve.
        /// </summary>
        public static LoadedConfig? TryImport(string path, List<string> warnings, out string? error)
        {
            error = null;

            try
            {
                var config = Parse(File.ReadAllText(path), warnings);
                if (config == null)
                    error = "El archivo no parece una configuración de MacroPad (no tiene perfiles ni teclas).";

                return config;
            }
            catch (JsonException)
            {
                error = "El archivo no es un JSON válido.";
                return null;
            }
            catch (Exception ex)
            {
                error = "No se pudo leer el archivo: " + ex.Message;
                return null;
            }
        }

        /// <summary>Copia la configuración actual a otro archivo.</summary>
        public static void Export(string destination)
        {
            Directory.CreateDirectory(ConfigFolder);
            File.Copy(ConfigPath, destination, overwrite: true);
        }

        public static void Save(LoadedConfig config)
        {
            Directory.CreateDirectory(ConfigFolder);

            // Copia de seguridad del archivo anterior (por si editaste algo a mano y no quieres perderlo)
            if (File.Exists(ConfigPath))
                File.Copy(ConfigPath, ConfigPath + ".bak", overwrite: true);

            var file = new AppConfig { PinnedProfile = config.PinnedProfile };

            foreach (var profile in config.Profiles)
            {
                var profileFile = new ProfileFile { Name = profile.Name, Apps = profile.Apps.ToList() };
                foreach (var pair in profile.Bindings)
                    profileFile.Bindings[pair.Key.ToString()] = pair.Value;

                file.Profiles.Add(profileFile);
            }

            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(file, Options));
        }

        /// <summary>Deja los nombres de programa en minúsculas y sin ".exe" (ej.: "EXCEL.EXE" -> "excel").</summary>
        public static List<string> NormalizeApps(IEnumerable<string>? apps)
        {
            var result = new List<string>();

            foreach (string raw in apps ?? Array.Empty<string>())
            {
                string name = Path.GetFileName(raw.Trim().Trim('"')).ToLowerInvariant();
                if (name.EndsWith(".exe")) name = name[..^4];

                if (name.Length > 0 && !result.Contains(name))
                    result.Add(name);
            }

            return result;
        }

        // Convierte el texto JSON en una configuración válida. Devuelve null si no hay perfiles ni teclas.
        private static LoadedConfig? Parse(string json, List<string> warnings)
        {
            var file = JsonSerializer.Deserialize<AppConfig>(json, Options);
            if (file == null) return null;

            file.Profiles ??= new();
            if (file.Profiles.Count == 0 && file.Bindings == null) return null;

            var config = new LoadedConfig();

            if (file.Profiles.Count == 0)
            {
                // Formato antiguo: todas las teclas pasan a un único perfil "General"
                config.Profiles.Add(new ProfileData
                {
                    Name = GeneralName,
                    Bindings = ParseBindings(file.Bindings!, warnings, GeneralName)
                });
                config.Migrated = true;
            }
            else
            {
                foreach (var profileFile in file.Profiles)
                {
                    string name = (profileFile.Name ?? "").Trim();

                    if (name.Length == 0)
                    {
                        warnings.Add("Hay un perfil sin nombre (se ignora).");
                        continue;
                    }
                    if (config.Profiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        warnings.Add($"El perfil «{name}» está repetido (se ignora el segundo).");
                        continue;
                    }

                    config.Profiles.Add(new ProfileData
                    {
                        Name = name,
                        Apps = NormalizeApps(profileFile.Apps),
                        Bindings = ParseBindings(profileFile.Bindings ?? new(), warnings, name)
                    });
                }
            }

            // "General" siempre existe y va primero
            int generalIndex = config.Profiles.FindIndex(p => p.Name.Equals(GeneralName, StringComparison.OrdinalIgnoreCase));
            if (generalIndex < 0)
            {
                config.Profiles.Insert(0, new ProfileData { Name = GeneralName });
            }
            else if (generalIndex > 0)
            {
                var general = config.Profiles[generalIndex];
                config.Profiles.RemoveAt(generalIndex);
                config.Profiles.Insert(0, general);
            }
            config.Profiles[0].Name = GeneralName;
            config.Profiles[0].Apps.Clear();   // General es el perfil de respaldo: no se activa por programa

            if (!string.IsNullOrWhiteSpace(file.PinnedProfile))
            {
                var pinned = config.Profiles.FirstOrDefault(p => p.Name.Equals(file.PinnedProfile, StringComparison.OrdinalIgnoreCase));
                if (pinned != null)
                    config.PinnedProfile = pinned.Name;
                else
                    warnings.Add($"El perfil fijado «{file.PinnedProfile}» no existe: vuelve el cambio automático.");
            }

            return config;
        }

        private static Dictionary<Key, MacroAction> ParseBindings(
            Dictionary<string, MacroAction> source, List<string> warnings, string profileName)
        {
            var result = new Dictionary<Key, MacroAction>();

            foreach (var pair in source)
            {
                if (Enum.TryParse<Key>(pair.Key, true, out var key))
                    result[key] = pair.Value;
                else
                    warnings.Add($"Tecla desconocida en el perfil «{profileName}»: \"{pair.Key}\" (se ignora).");
            }

            return result;
        }
    }
}
