using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Grid = System.Windows.Controls.Grid;
using SelectionChangedEventArgs = System.Windows.Controls.SelectionChangedEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using Point = System.Windows.Point;
using ScaleTransform = System.Windows.Media.ScaleTransform;
using TranslateTransform = System.Windows.Media.TranslateTransform;
using TransformGroup = System.Windows.Media.TransformGroup;
using ColorAnimation = System.Windows.Media.Animation.ColorAnimation;
using DoubleAnimation = System.Windows.Media.Animation.DoubleAnimation;
using QuadraticEase = System.Windows.Media.Animation.QuadraticEase;
using EasingMode = System.Windows.Media.Animation.EasingMode;
using TabItem = System.Windows.Controls.TabItem;
using DropShadowEffect = System.Windows.Media.Effects.DropShadowEffect;
using RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace MacroPad
{
    /// <summary>Opción del desplegable "Tipo de acción".</summary>
    public record TypeOption(ActionType Type, string Label);

    public partial class MainWindow : Window
    {
        private const int VK_NUMLOCK = 0x90;
        private const int VK_RETURN = 0x0D;

        // Diagnóstico: muestra en el registro las teclas del bloque numérico que ve el hook
        private const bool Diagnostico = false;

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int nVirtKey);

        private readonly RawInputManager _raw = new();      // solo para listar teclados
        private readonly KeyboardHook _hook = new();
        private readonly ObservableCollection<string> _log = new();
        private readonly HashSet<int> _held = new();        // teclas bloqueadas que siguen presionadas
        private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
        private bool? _lastMacroState;

        // Bandeja del sistema
        private System.Windows.Forms.NotifyIcon? _tray;
        private bool _exiting;
        private bool _balloonShown;
        private bool _loadingUi;

        // ----- Teclado dibujado -----
        private record KeyDef(Key? Id, string Label, int Row, int Col, int RowSpan = 1, int ColSpan = 1, string? Note = null);

        // Distribución del Cybertel (Id = null: tecla que no se puede asignar)
        private static readonly KeyDef[] Layout =
        {
            new(null,         "Num Lock",  0, 0, Note: "Modo macro"),
            new(Key.Divide,   "/",         0, 1),
            new(Key.Multiply, "*",         0, 2),
            new(Key.Subtract, "−",         0, 3),
            new(Key.NumPad7,  "7",         1, 0),
            new(Key.NumPad8,  "8",         1, 1),
            new(Key.NumPad9,  "9",         1, 2),
            new(Key.Add,      "+",         1, 3),
            new(Key.NumPad4,  "4",         2, 0),
            new(Key.NumPad5,  "5",         2, 1),
            new(Key.NumPad6,  "6",         2, 2),
            new(null,         "Backspace", 2, 3, Note: "No asignable"),
            new(Key.NumPad1,  "1",         3, 0),
            new(Key.NumPad2,  "2",         3, 1),
            new(Key.NumPad3,  "3",         3, 2),
            new(Key.Enter,    "Enter",     3, 3, RowSpan: 2),
            new(Key.NumPad0,  "0",         4, 0, ColSpan: 2),
            new(Key.Decimal,  ".",         4, 2),
        };

        // Cada tecla dibujada guarda lo necesario para animarla
        private sealed class KeyVisual
        {
            public KeyDef Def = null!;
            public Button Btn = null!;
            public ScaleTransform Scale = null!;
            public SolidColorBrush Bg = null!;
            public SolidColorBrush Border = null!;
            public Color BgTarget;      // color de fondo "en reposo" de esta tecla
            public Color BorderTarget;  // color de borde "en reposo"
            public DropShadowEffect Glow = null!;
            public double GlowTarget;   // intensidad del resplandor "en reposo"
            public bool Hover;
        }

        private readonly List<KeyVisual> _keys = new();
        private System.Drawing.Icon? _appIcon;
        private Key? _selectedKey;

        // Con Num Lock APAGADO, el bloque numérico envía teclas de navegación (sin el flag "extendida").
        // Las traducimos a NumPadN para usar la misma tabla de acciones.
        private static readonly Dictionary<int, Key> NavToPad = new()
        {
            [0x2D] = Key.NumPad0,   // Ins
            [0x23] = Key.NumPad1,   // End
            [0x28] = Key.NumPad2,   // Abajo
            [0x22] = Key.NumPad3,   // PgDn
            [0x25] = Key.NumPad4,   // Izquierda
            [0x0C] = Key.NumPad5,   // Clear
            [0x27] = Key.NumPad6,   // Derecha
            [0x24] = Key.NumPad7,   // Home
            [0x26] = Key.NumPad8,   // Arriba
            [0x21] = Key.NumPad9,   // PgUp
            [0x2E] = Key.Decimal,   // Del
        };

        // Tabla tecla -> acción: se carga desde %AppData%\MacroPad\config.json
        // ----- Perfiles -----
        private LoadedConfig _config = null!;
        private ProfileData _editProfile = null!;                  // perfil que se está editando en pantalla
        private Dictionary<Key, MacroAction> _bindings => _editProfile.Bindings;
        private bool _syncingProfileUi;
        private uint _lastForegroundPid;
        private string _lastForegroundName = "";
        private string? _lastProfileText;
        private readonly string _selfProcessName = Process.GetCurrentProcess().ProcessName.ToLowerInvariant();

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        public MainWindow()
        {
            InitializeComponent();

            // En pantallas pequeñas, la ventana no debe pasar del área de trabajo
            Height = Math.Min(Height, SystemParameters.WorkArea.Height - 40);

            _config = ConfigManager.Defaults();
            _editProfile = _config.General;

            LogList.ItemsSource = _log;

            TypeCombo.ItemsSource = new[]
            {
                new TypeOption(ActionType.OpenProgram,  "Abrir programa"),
                new TypeOption(ActionType.FocusProgram, "Abrir o enfocar programa"),
                new TypeOption(ActionType.OpenUrl,      "Abrir página web (URL)"),
                new TypeOption(ActionType.SendShortcut, "Atajo de teclado"),
                new TypeOption(ActionType.TypeText,     "Escribir texto"),
                new TypeOption(ActionType.Sequence,     "Macro de varios pasos"),
            };
            BuildKeyboard();

            _appIcon = AppIcon.Create();
            Icon = AppIcon.ToImageSource(_appIcon);
            HeaderIcon.Source = Icon;
            CreateTrayIcon();

            _statusTimer.Tick += (_, _) =>
            {
                UpdateStatus();
                UpdateProfileIndicator();
            };
            SourceInitialized += (_, _) => EnableDarkTitleBar();
            Loaded += OnLoaded;
            PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape) return;

                if (PromptOverlay.Visibility == Visibility.Visible) { HidePrompt(); e.Handled = true; }
                else if (PickerOverlay.Visibility == Visibility.Visible) { HidePicker(); e.Handled = true; }
            };
            Closing += OnWindowClosing;
            StateChanged += (_, _) =>
            {
                if (WindowState == WindowState.Minimized) HideToTray(showBalloon: true);
            };

            // Si Windows se está apagando o cerrando sesión, no debemos impedirlo
            System.Windows.Application.Current.SessionEnding += (_, _) => _exiting = true;

            Closed += (_, _) =>
            {
                _statusTimer.Stop();
                _hook.Dispose();   // al cerrar, se libera el hook y el teclado vuelve a la normalidad
                if (_tray != null)
                {
                    _tray.Visible = false;
                    _tray.Dispose();
                }
            };
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            RefreshDevices();
            bool configOk = LoadConfig();

            try
            {
                _hook.KeyEvent = OnHookKey;
                _hook.Start();
                AddLog("Hook de teclado activo.");
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "MacroPad", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // Estado inicial de la casilla de inicio automático (sin disparar el evento)
            _loadingUi = true;
            try { AutoStartCheck.IsChecked = AutoStart.IsEnabled(); }
            catch { /* si no se puede leer el registro, se deja sin marcar */ }
            _loadingUi = false;

            UpdateStatus();
            _statusTimer.Start();

            if (!configOk)
                ShowToast("Hay avisos en config.json. Revisa el registro.", ToastKind.Warning);

            // Si Windows nos arrancó con --minimized, nos quedamos en la bandeja
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--minimized") >= 0)
                HideToTray(showBalloon: false);
        }

        // ---------- Bandeja del sistema ----------

        private void CreateTrayIcon()
        {
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Abrir MacroPad", null, (_, _) => ShowFromTray());
            menu.Items.Add("Alternar modo macro", null, (_, _) => ToggleMacroMode());
            menu.Items.Add("Recargar configuración", null, (_, _) => LoadConfig());

            var profileMenu = new System.Windows.Forms.ToolStripMenuItem("Perfil");
            menu.Items.Add(profileMenu);
            menu.Opening += (_, _) => RebuildProfileMenu(profileMenu);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Salir", null, (_, _) => ExitApp());

            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = _appIcon ?? System.Drawing.SystemIcons.Application,
                Text = "MacroPad",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => ShowFromTray();
        }

        public void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        private void HideToTray(bool showBalloon)
        {
            Hide();

            if (showBalloon && !_balloonShown)
            {
                _balloonShown = true;
                _tray?.ShowBalloonTip(2500, "MacroPad",
                    "Sigue activo en segundo plano. Doble clic en el ícono para abrirlo.",
                    System.Windows.Forms.ToolTipIcon.Info);
            }
        }

        // La X de la ventana solo la oculta; para salir de verdad se usa el menú "Salir" del ícono
        private void OnWindowClosing(object? sender, CancelEventArgs e)
        {
            if (_exiting) return;

            e.Cancel = true;
            HideToTray(showBalloon: true);
        }

        private void ExitApp()
        {
            _exiting = true;
            Close();
        }

        // ---------- Modo macro ----------

        // El modo macro depende SIEMPRE del estado real de Num Lock en Windows: apagado = modo macro
        private static bool IsMacroMode() => (GetKeyState(VK_NUMLOCK) & 1) == 0;

        private void UpdateStatus()
        {
            bool macro = IsMacroMode();
            if (_lastMacroState == macro) return;
            _lastMacroState = macro;

            StatusText.Text = macro ? "Modo macro activado" : "Modo macro desactivado";
            StatusHint.Text = macro
                ? "Num Lock apagado · el bloque numérico ejecuta tus acciones"
                : "Num Lock encendido · el bloque numérico escribe números";

            AnimateColor(PillBg,     macro ? Hex(0x12223F) : Hex(0x141B2D));
            AnimateColor(PillBorder, macro ? Hex(0x3B82F6) : Hex(0x2A3857));
            AnimateColor(PillDot,    macro ? Hex(0x3B82F6) : Hex(0x6B7A99));
            AnimateColor(PillText,   macro ? Hex(0xE5EAF5) : Hex(0x9AA6BF));
            DotGlow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(macro ? 0.9 : 0, TimeSpan.FromMilliseconds(300)));
            if (macro) StartPulse(); else StopPulse();

            UpdateTrayText();

            AddLog(macro ? "Modo macro ACTIVADO" : "Modo macro DESACTIVADO");
        }

        private void ToggleMacroMode()
        {
            try
            {
                // Alterna el estado real de Num Lock (el indicador se actualiza solo)
                ActionEngine.Execute(new MacroAction { Type = ActionType.SendShortcut, Value = "NumLock" });
            }
            catch (Exception ex)
            {
                AddLog("✖ no se pudo alternar Num Lock: " + ex.Message);
            }
        }

        // ---------- Hook de teclado ----------

        // Se ejecuta en el hilo de la interfaz y debe ser rápido. Devuelve true para BLOQUEAR la tecla.
        private bool OnHookKey(HookKeyEventArgs e)
        {
            try
            {
                if (Diagnostico && IsDiagnosticKey(e))
                    AddLog($"[diag] {(e.IsDown ? "↓" : "↑")} vk=0x{e.VKey:X2} ext={e.IsExtended} modoMacro={(IsMacroMode() ? "ON" : "OFF")}");

                return ProcessKey(e);
            }
            catch (Exception ex)
            {
                AddLog("✖ error en el hook: " + ex.Message);
                return false;   // ante un error, nunca bloqueamos
            }
        }

        private bool ProcessKey(HookKeyEventArgs e)
        {
            // Soltar tecla: se bloquea solo si bloqueamos la pulsación correspondiente
            if (!e.IsDown)
                return _held.Remove(e.VKey);

            // Con Num Lock encendido todo pasa con normalidad
            if (!IsMacroMode())
                return false;

            Key? pad = ToPadKey(e);
            if (pad == null)
                return false;

            // Primera pulsación (se ignoran las repeticiones al mantener presionada)
            if (_held.Add(e.VKey))
                RunBinding(pad.Value);

            return true;
        }

        // Traduce lo que envía el bloque numérico (con Num Lock apagado) a una tecla NumPad.
        // Las teclas de navegación "normales" (cruceta, Inicio, Fin...) vienen con el flag extendido y no se tocan.
        private static Key? ToPadKey(HookKeyEventArgs e)
        {
            int v = e.VKey;

            if (v >= 0x60 && v <= 0x6F)                       // NumPad0-9 y * + , - . /
                return KeyInterop.KeyFromVirtualKey(v);

            if (v == VK_RETURN && e.IsExtended)               // Enter del bloque numérico
                return Key.Enter;

            if (!e.IsExtended && NavToPad.TryGetValue(v, out var key))
                return key;

            return null;
        }

        private static bool IsDiagnosticKey(HookKeyEventArgs e)
        {
            int v = e.VKey;
            return (v >= 0x60 && v <= 0x6F)
                || (v >= 0x21 && v <= 0x28)
                || v == 0x2D || v == 0x2E || v == 0x0C
                || v == VK_NUMLOCK
                || (v == VK_RETURN && e.IsExtended);
        }

        private void RunBinding(Key key)
        {
            FlashKey(key);

            // Perfil activo (fijado a mano o según la ventana activa); si no tiene la tecla, se usa la de General
            var profile = GetActiveProfile(out _);
            MacroAction? found = null;

            if (profile.Bindings.TryGetValue(key, out var own))
                found = own;
            else if (!ReferenceEquals(profile, _config.General) && _config.General.Bindings.TryGetValue(key, out var inherited))
                found = inherited;

            if (found == null)
            {
                AddLog($"{key}  (sin acción asignada)");
                return;
            }

            MacroAction action = found;
            string profileTag = ReferenceEquals(profile, _config.General) ? "" : $"  [{profile.Name}]";
            AddLog($"{key}  → {action.Name}{profileTag}");

            // Se ejecuta en segundo plano para que el hook responda de inmediato
            _ = Task.Run(() =>
            {
                try { ActionEngine.Execute(action); }
                catch (Exception ex)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        AddLog($"✖ {action.Name}: {ex.Message}");
                        FlashError(key);
                        ShowToast($"{action.Name}: {ex.Message}", ToastKind.Error);
                    });
                }
            });
        }

        // ---------- Configuración ----------

        private bool LoadConfig()
        {
            var warnings = new List<string>();
            string previousProfile = _editProfile.Name;

            _config = ConfigManager.Load(warnings);
            _editProfile = _config.Profiles.FirstOrDefault(p => p.Name.Equals(previousProfile, StringComparison.OrdinalIgnoreCase))
                           ?? _config.General;

            int total = _config.Profiles.Sum(p => p.Bindings.Count);
            AddLog($"Configuración cargada: {_config.Profiles.Count} perfil(es), {total} teclas asignadas.");
            AddLog("Archivo: " + ConfigManager.ConfigPath);
            if (_config.Migrated)
                AddLog("Configuración actualizada al formato con perfiles (la versión anterior quedó en config.json.bak).");
            foreach (var w in warnings)
                AddLog("⚠ " + w);

            RefreshProfileUi();
            if (_selectedKey is Key selected) SelectKey(selected);
            else RefreshKeyboard();
            UpdateProfileIndicator(force: true);

            return warnings.Count == 0;
        }

        // ---------- Teclado dibujado y panel de edición ----------

        private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        private static Brush Rgb(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        // Paleta del tema oscuro
        private static readonly Color AccentColor = Hex(0x3B82F6);
        private static readonly Color KeyEmptyBg = Hex(0x1A2438), KeyEmptyBorder = Hex(0x26334D);
        private static readonly Color AssignedBg = Hex(0x1D2940), AssignedBorder = Hex(0x33558C);
        private static readonly Color SelectedBg = Hex(0x233A63);
        private static readonly Color DisabledBg = Hex(0x131A2B), DisabledBorder = Hex(0x1E2940);
        private static readonly Color ErrorBg = Hex(0x4A1F2A), ErrorBorder = Hex(0xF87171);

        private static readonly Brush LabelInk = Rgb(0xE5, 0xEA, 0xF5);
        private static readonly Brush LabelDisabled = Rgb(0x4C, 0x59, 0x74);
        private static readonly Brush SubMuted = Rgb(0x7C, 0x8A, 0xA5);
        private static readonly Brush SubAccent = Rgb(0x8F, 0xB8, 0xFF);

        private static readonly QuadraticEase EaseOut = new() { EasingMode = EasingMode.EaseOut };

        // ----- Helpers de animación -----

        private static void AnimateColor(SolidColorBrush brush, Color to, int ms = 220)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        private static void AnimateScale(KeyVisual key, double to, int ms)
        {
            var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
            key.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            key.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        private static void AnimateGlow(KeyVisual key, double to, int ms = 180)
        {
            key.Glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut });
        }

        // ----- Construcción del teclado -----

        private void BuildKeyboard()
        {
            var style = (Style)FindResource("KeyButton");
            int index = 0;

            foreach (var def in Layout)
            {
                var scale = new ScaleTransform(1, 1);
                var move = new TranslateTransform(0, 14);
                var transforms = new TransformGroup();
                transforms.Children.Add(scale);
                transforms.Children.Add(move);

                var glow = new DropShadowEffect { Color = AccentColor, BlurRadius = 18, ShadowDepth = 0, Opacity = 0 };

                var visual = new KeyVisual
                {
                    Def = def,
                    Scale = scale,
                    Glow = glow,
                    Bg = new SolidColorBrush(KeyEmptyBg),
                    Border = new SolidColorBrush(KeyEmptyBorder),
                    BgTarget = KeyEmptyBg,
                    BorderTarget = KeyEmptyBorder
                };

                var btn = new Button
                {
                    Style = style,
                    Margin = new Thickness(4),
                    Background = visual.Bg,
                    BorderBrush = visual.Border,
                    BorderThickness = new Thickness(1),
                    RenderTransformOrigin = new Point(0.5, 0.5),
                    RenderTransform = transforms,
                    Effect = glow,
                    Opacity = 0
                };
                visual.Btn = btn;

                Grid.SetRow(btn, def.Row);
                Grid.SetColumn(btn, def.Col);
                Grid.SetRowSpan(btn, def.RowSpan);
                Grid.SetColumnSpan(btn, def.ColSpan);

                if (def.Id is Key key)
                {
                    btn.Click += (_, _) => SelectKey(key);

                    // Al pasar el mouse la tecla se ilumina y crece un poco; al presionar se comprime
                    btn.MouseEnter += (_, _) =>
                    {
                        visual.Hover = true;
                        AnimateScale(visual, 1.05, 140);
                        AnimateGlow(visual, Math.Max(visual.GlowTarget, 0.35));
                    };
                    btn.MouseLeave += (_, _) =>
                    {
                        visual.Hover = false;
                        AnimateScale(visual, 1.0, 180);
                        AnimateGlow(visual, visual.GlowTarget);
                    };
                    btn.PreviewMouseLeftButtonDown += (_, _) => AnimateScale(visual, 0.94, 80);
                    btn.PreviewMouseLeftButtonUp += (_, _) => AnimateScale(visual, visual.Hover ? 1.05 : 1.0, 140);
                }
                else
                {
                    btn.IsEnabled = false;   // Num Lock y Backspace no se pueden asignar
                }

                KeyGrid.Children.Add(btn);
                _keys.Add(visual);

                // Aparición escalonada: cada tecla sube y se desvanece hacia adentro, una tras otra
                var delay = TimeSpan.FromMilliseconds(80 + index * 40);
                btn.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { BeginTime = delay });
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(420)) { BeginTime = delay, EasingFunction = EaseOut });
                index++;
            }

            RefreshKeyboard(animate: false);
        }

        // Repinta cada tecla según su estado: sin asignar, asignada, seleccionada o no asignable
        private void RefreshKeyboard(bool animate = true)
        {
            foreach (var k in _keys)
            {
                var def = k.Def;
                string? actionName = null;
                string? inheritedName = null;
                bool assigned = false;

                if (def.Id is Key key && _bindings.TryGetValue(key, out var action))
                {
                    assigned = true;
                    actionName = string.IsNullOrWhiteSpace(action.Name) ? action.Value : action.Name;
                }
                else if (def.Id is Key inheritedKey && !ReferenceEquals(_editProfile, _config.General)
                         && _config.General.Bindings.TryGetValue(inheritedKey, out var inheritedAction))
                {
                    inheritedName = string.IsNullOrWhiteSpace(inheritedAction.Name) ? inheritedAction.Value : inheritedAction.Name;
                }

                bool selected = def.Id != null && def.Id == _selectedKey;
                bool disabled = def.Id == null;

                Color bg, border;
                double glow;
                if (disabled)      { bg = DisabledBg;  border = DisabledBorder;  glow = 0; }
                else if (selected) { bg = SelectedBg;  border = AccentColor;     glow = 0.8; }
                else if (assigned) { bg = AssignedBg;  border = AssignedBorder;  glow = 0.2; }
                else               { bg = KeyEmptyBg;  border = KeyEmptyBorder;  glow = 0; }

                var panel = new StackPanel();
                panel.Children.Add(new TextBlock
                {
                    Text = def.Label,
                    FontSize = def.Label.Length > 2 ? 14 : 22,
                    FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center,
                    Foreground = disabled ? LabelDisabled : LabelInk
                });

                string sub = actionName ?? (inheritedName != null ? "↩ " + inheritedName : null) ?? def.Note ?? "";
                if (sub.Length > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = sub,
                        FontSize = 10,
                        Margin = new Thickness(0, 2, 0, 0),
                        TextAlignment = TextAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Foreground = disabled ? LabelDisabled : (assigned ? SubAccent : SubMuted)
                    });
                }

                k.Btn.Content = panel;
                k.Btn.ToolTip = assigned ? actionName
                              : inheritedName != null ? $"Heredada de General: {inheritedName}"
                              : def.Note;
                k.Btn.BorderThickness = new Thickness(selected ? 2 : 1);
                k.BgTarget = bg;
                k.BorderTarget = border;
                k.GlowTarget = glow;

                if (animate)
                {
                    AnimateColor(k.Bg, bg);
                    AnimateColor(k.Border, border);
                    AnimateGlow(k, k.Hover ? Math.Max(glow, 0.35) : glow);
                }
                else
                {
                    k.Bg.Color = bg;
                    k.Border.Color = border;
                    k.Glow.Opacity = glow;
                }
            }
        }

        // Destello azul cuando presionas la tecla en el teclado real
        private void FlashKey(Key key)
        {
            foreach (var k in _keys)
            {
                if (k.Def.Id != key) continue;

                k.Bg.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(AccentColor, k.BgTarget, TimeSpan.FromMilliseconds(600)) { EasingFunction = EaseOut });
                k.Glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(1.0, k.GlowTarget, TimeSpan.FromMilliseconds(650)) { EasingFunction = EaseOut });

                double rest = k.Hover ? 1.05 : 1.0;
                var pulse = new DoubleAnimation(0.9, rest, TimeSpan.FromMilliseconds(320)) { EasingFunction = EaseOut };
                k.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
                k.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
                return;
            }
        }

        // Destello rojo cuando una acción falla
        private void FlashError(Key key)
        {
            foreach (var k in _keys)
            {
                if (k.Def.Id != key) continue;

                k.Bg.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(ErrorBg, k.BgTarget, TimeSpan.FromMilliseconds(1100)) { EasingFunction = EaseOut });
                k.Border.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(ErrorBorder, k.BorderTarget, TimeSpan.FromMilliseconds(1100)) { EasingFunction = EaseOut });
                k.Glow.BeginAnimation(DropShadowEffect.ColorProperty,
                    new ColorAnimation(ErrorBorder, AccentColor, TimeSpan.FromMilliseconds(1100)));
                k.Glow.BeginAnimation(DropShadowEffect.OpacityProperty,
                    new DoubleAnimation(1.0, k.GlowTarget, TimeSpan.FromMilliseconds(1100)) { EasingFunction = EaseOut });
                return;
            }
        }

        private static string KeyLabel(Key key)
        {
            foreach (var def in Layout)
                if (def.Id == key) return def.Label;
            return key.ToString();
        }

        private void SelectKey(Key key)
        {
            _selectedKey = key;
            EditPanel.IsEnabled = true;
            SelectedKeyText.Text = $"Tecla seleccionada:  {KeyLabel(key)}";

            if (_bindings.TryGetValue(key, out var action))
            {
                NameBox.Text = action.Name;
                TypeCombo.SelectedValue = action.Type;
                ValueBox.Text = action.Value;
            }
            else
            {
                NameBox.Text = "";
                TypeCombo.SelectedValue = ActionType.OpenProgram;
                ValueBox.Text = "";
            }

            string inheritedNote = "";
            if (!ReferenceEquals(_editProfile, _config.General) && !_bindings.ContainsKey(key)
                && _config.General.Bindings.TryGetValue(key, out var generalAction))
                inheritedNote = $"  ·  hereda «{generalAction.Name}» de General";

            SelectedKeyText.Text = $"Tecla seleccionada:  {KeyLabel(key)}  ·  perfil {_editProfile.Name}{inheritedNote}";

            UpdateHints();
            RefreshKeyboard();

            EditPanel.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(220)));
        }

        private void TypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateHints();

        // Ayuda según el tipo de acción; el botón "Examinar" solo aplica a programas
        private void UpdateHints()
        {
            if (HintText == null || BrowseButton == null) return;

            ActionType? type = TypeCombo.SelectedValue is ActionType t ? t : null;

            ProgramTools.Visibility = (type == ActionType.OpenProgram || type == ActionType.FocusProgram || type == ActionType.Sequence)
                ? Visibility.Visible : Visibility.Collapsed;

            // Las macros de varios pasos se escriben en un cuadro de varias líneas
            bool multiline = type == ActionType.Sequence;
            BrowseLabel.Text = multiline ? "Añadir programa" : "Examinar...";
            PickLabel.Text = multiline ? "Añadir ventana abierta" : "Programas abiertos";
            ValueBox.AcceptsReturn = multiline;
            ValueBox.TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;
            ValueBox.MinHeight = multiline ? 140 : 0;
            ValueBox.VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;

            HintText.Text = type switch
            {
                ActionType.OpenProgram  => "Nombre o ruta del programa. Ej.: calc.exe o C:\\Programas\\app.exe",
                ActionType.FocusProgram => "Si el programa ya tiene una ventana abierta, la trae al frente; si no, lo abre. Usa Examinar para elegir el .exe.",
                ActionType.Sequence     => "Un paso por línea: abrir: programa · enfocar: programa · url: dirección · atajo: Ctrl+S · escribir: texto · esperar: 1s (o 500ms). Las líneas con # son comentarios.",
                ActionType.OpenUrl      => "Dirección web completa. Ej.: https://www.google.com",
                ActionType.SendShortcut => "Teclas unidas con +. Ej.: Ctrl+C, Ctrl+Shift+Esc, Win+D",
                ActionType.TypeText     => "Texto que se escribirá tal cual en la ventana activa.",
                _ => ""
            };

            ValueBox.Tag = type switch
            {
                ActionType.OpenProgram or ActionType.FocusProgram => "Ruta del programa o nombre del .exe...",
                ActionType.Sequence     => "abrir: notepad.exe\nesperar: 1s\nescribir: Hola",
                ActionType.OpenUrl      => "https://...",
                ActionType.SendShortcut => "Ctrl+Shift+Esc",
                ActionType.TypeText     => "Texto que se escribirá...",
                _ => "Ruta del programa o URL..."
            };
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Elige un programa",
                Filter = "Programas (*.exe)|*.exe|Todos los archivos (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            if (TypeCombo.SelectedValue is ActionType type && type == ActionType.Sequence)
            {
                AppendScriptLine($"abrir: {dialog.FileName}");
                return;
            }

            ValueBox.Text = dialog.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        }

        // Añade una línea al final de la macro y deja el cursor ahí
        private void AppendScriptLine(string line)
        {
            string text = ValueBox.Text;
            if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";

            ValueBox.Text = text + line;
            ValueBox.CaretIndex = ValueBox.Text.Length;
            ValueBox.ScrollToEnd();
            ValueBox.Focus();
        }

        private void ShowFeedback(string message, bool ok) =>
            ShowToast(message, ok ? ToastKind.Success : ToastKind.Error);

        // Lee y valida los campos del panel
        private MacroAction? ReadFields(out string error)
        {
            error = "";

            if (TypeCombo.SelectedValue is not ActionType type)
            {
                error = "Elige un tipo de acción.";
                return null;
            }

            string value = type == ActionType.TypeText ? ValueBox.Text : ValueBox.Text.Trim();
            if (value.Length == 0)
            {
                error = "Escribe el valor de la acción.";
                return null;
            }

            if (type == ActionType.Sequence)
            {
                ActionEngine.ParseSequence(value, out var sequenceError);
                if (sequenceError != null)
                {
                    error = sequenceError;
                    return null;
                }
            }

            string? programError = ValidatePrograms(type, value);
            if (programError != null)
            {
                error = programError;
                return null;
            }

            string name = NameBox.Text.Trim();
            if (name.Length == 0) name = type == ActionType.Sequence ? "Macro" : value;

            return new MacroAction { Name = name, Type = type, Value = value };
        }

        private async void TestButton_Click(object sender, RoutedEventArgs e)
        {
            var action = ReadFields(out var error);
            if (action == null)
            {
                ShowFeedback(error, false);
                return;
            }

            // Atajos y texto se envían a la ventana activa: damos tiempo para hacer clic en la ventana destino
            if (action.Type == ActionType.SendShortcut || action.Type == ActionType.TypeText || action.Type == ActionType.Sequence)
            {
                ShowToast("Se ejecutará en 3 segundos: haz clic en la ventana donde quieres probarlo.", ToastKind.Info);
                await Task.Delay(3000);
            }

            try
            {
                await Task.Run(() => ActionEngine.Execute(action));
                ShowFeedback("Acción ejecutada.", true);
            }
            catch (Exception ex)
            {
                ShowFeedback("Error: " + ex.Message, false);
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedKey is not Key key) return;

            var action = ReadFields(out var error);
            if (action == null)
            {
                ShowFeedback(error, false);
                return;
            }

            _bindings[key] = action;
            if (!TrySaveConfig()) return;

            AddLog($"Guardado: {KeyLabel(key)} → {action.Name}");
            ShowFeedback("Guardado. Ya está activo.", true);
            RefreshKeyboard();
        }

        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedKey is not Key key) return;

            if (!_bindings.Remove(key))
            {
                ShowFeedback("Esta tecla no tiene ninguna acción asignada.", false);
                return;
            }

            if (!TrySaveConfig()) return;

            NameBox.Text = "";
            ValueBox.Text = "";
            TypeCombo.SelectedValue = ActionType.OpenProgram;

            AddLog($"Asignación quitada: {KeyLabel(key)}");
            ShowFeedback("Asignación quitada.", true);
            RefreshKeyboard();
        }

        private bool TrySaveConfig()
        {
            try
            {
                ConfigManager.Save(_config);
                return true;
            }
            catch (Exception ex)
            {
                ShowFeedback("No se pudo guardar config.json: " + ex.Message, false);
                return false;
            }
        }

        // ---------- Detalles visuales: barra de título, pulso, notificaciones y pestañas ----------

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private void EnableDarkTitleBar()
        {
            try
            {
                IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                int on = 1;

                // 20 en Windows 10 (2004 o superior) y Windows 11; 19 en versiones anteriores de Windows 10
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
            }
            catch { /* si no está disponible, la barra de título conserva el tema de Windows */ }
        }

        // Anillo de luz que se expande lentamente alrededor del punto cuando el modo macro está activo
        private void StartPulse()
        {
            var grow = new DoubleAnimation(1, 2.6, TimeSpan.FromMilliseconds(1900))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = EaseOut
            };
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);

            PulseRing.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.55, 0, TimeSpan.FromMilliseconds(1900)) { RepeatBehavior = RepeatBehavior.Forever });
        }

        private void StopPulse()
        {
            PulseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            PulseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            PulseRing.BeginAnimation(UIElement.OpacityProperty, null);
            PulseRing.Opacity = 0;
        }

        // Notificación que entra desde la esquina inferior derecha
        private enum ToastKind { Info, Success, Warning, Error }

        private int _toastVersion;

        private async void ShowToast(string message, ToastKind kind)
        {
            int version = ++_toastVersion;

            (string glyph, Color color, Color tint) = kind switch
            {
                ToastKind.Success => ("\uE73E", Hex(0x34D399), Hex(0x123D31)),
                ToastKind.Warning => ("\uE7BA", Hex(0xFBBF24), Hex(0x3F3414)),
                ToastKind.Error   => ("\uE711", Hex(0xF87171), Hex(0x43202A)),
                _                 => ("\uE946", AccentColor,   Hex(0x17284A)),
            };

            ToastIcon.Text = glyph;
            ToastColor.Color = color;
            ToastTint.Color = tint;
            ToastText.Text = message;

            Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            ToastMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = EaseOut });

            await Task.Delay((kind == ToastKind.Error ? 4200 : 2800) + Math.Max(0, message.Length - 60) * 45);
            if (version != _toastVersion) return;   // llegó otra notificación: esa se encarga de ocultarse

            Toast.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
            ToastMove.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, 24, TimeSpan.FromMilliseconds(300)));
        }

        // Al cambiar de pestaña, el contenido aparece con un fundido y un desplazamiento mínimo
        private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Este evento también llega desde las listas internas; solo nos interesa el cambio de pestaña
            if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;

            if (MainTabs.SelectedItem is TabItem { Content: FrameworkElement content })
            {
                var move = new TranslateTransform(0, 10);
                content.RenderTransform = move;

                content.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
                move.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = EaseOut });
            }
        }

        // ---------- Elegir programas sin escribirlos ----------

        private Action<RunningApp>? _pickerCallback;
        private List<RunningApp> _pickerItems = new();

        // Comprueba que los programas existan; si hay una errata, propone el nombre correcto
        private static string? ValidatePrograms(ActionType type, string value)
        {
            if (type == ActionType.OpenProgram || type == ActionType.FocusProgram)
                return ProgramResolver.Check(value, allowRunning: type == ActionType.FocusProgram);

            if (type == ActionType.Sequence)
            {
                foreach (var step in ActionEngine.ParseSequence(value, out _))
                {
                    if (step.Type != ActionType.OpenProgram && step.Type != ActionType.FocusProgram) continue;

                    string? problem = ProgramResolver.Check(step.Value, allowRunning: step.Type == ActionType.FocusProgram);
                    if (problem != null) return $"Línea {step.Line}: {problem}";
                }
            }

            return null;
        }

        private void PickButton_Click(object sender, RoutedEventArgs e)
        {
            bool sequence = TypeCombo.SelectedValue is ActionType type && type == ActionType.Sequence;

            ShowPicker(app =>
            {
                if (sequence)
                {
                    AppendScriptLine($"enfocar: {app.Value}");
                    return;
                }

                ValueBox.Text = app.Value;
                if (string.IsNullOrWhiteSpace(NameBox.Text))
                    NameBox.Text = app.Title;
            });
        }

        private async void ShowPicker(Action<RunningApp> onPick)
        {
            _pickerCallback = onPick;
            PickerSearch.Text = "";
            PickerList.ItemsSource = null;
            PickerStatus.Text = "Buscando programas abiertos...";

            PickerOverlay.Visibility = Visibility.Visible;
            PickerOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            PickerSearch.Focus();

            _pickerItems = await Task.Run(RunningApps.List);
            ApplyPickerFilter();
        }

        private void HidePicker()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => PickerOverlay.Visibility = Visibility.Collapsed;
            PickerOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void ApplyPickerFilter()
        {
            string filter = PickerSearch.Text.Trim();

            var shown = _pickerItems
                .Where(a => filter.Length == 0
                         || a.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                         || a.WindowTitle.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                         || a.Value.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            PickerList.ItemsSource = shown;
            if (shown.Count > 0) PickerList.SelectedIndex = 0;

            PickerStatus.Text = _pickerItems.Count == 0
                ? "No encontré programas con ventana abierta."
                : $"{shown.Count} de {_pickerItems.Count} programas";
        }

        private void PickerSearch_TextChanged(object sender, TextChangedEventArgs e) => ApplyPickerFilter();

        private void PickerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PickerList.SelectedItem != null) UsePickedApp();
        }

        private void PickerUse_Click(object sender, RoutedEventArgs e)
        {
            if (PickerList.SelectedItem == null)
            {
                ShowToast("Elige un programa de la lista.", ToastKind.Warning);
                return;
            }
            UsePickedApp();
        }

        private void PickerCancel_Click(object sender, RoutedEventArgs e) => HidePicker();

        private void UsePickedApp()
        {
            if (PickerList.SelectedItem is not RunningApp app) return;

            HidePicker();
            _pickerCallback?.Invoke(app);
        }

        // ---------- Perfiles ----------

        // Nombre (en minúsculas) del programa que tiene la ventana activa
        private string GetForegroundProcessName()
        {
            try
            {
                IntPtr window = GetForegroundWindow();
                if (window == IntPtr.Zero) return "";

                GetWindowThreadProcessId(window, out uint pid);
                if (pid == _lastForegroundPid) return _lastForegroundName;

                using var process = Process.GetProcessById((int)pid);
                _lastForegroundPid = pid;
                _lastForegroundName = process.ProcessName.ToLowerInvariant();
                return _lastForegroundName;
            }
            catch
            {
                _lastForegroundPid = 0;
                return "";
            }
        }

        // Perfil que debe ejecutar las teclas ahora. mode = "fijado", "automático" o "edición"
        private ProfileData GetActiveProfile(out string mode)
        {
            if (_config.PinnedProfile != null)
            {
                var pinned = _config.Profiles.FirstOrDefault(p => p.Name == _config.PinnedProfile);
                if (pinned != null)
                {
                    mode = "fijado";
                    return pinned;
                }
            }

            string app = GetForegroundProcessName();

            // Con la ventana de MacroPad al frente se usa el perfil que estás editando
            if (app == _selfProcessName)
            {
                mode = "edición";
                return _editProfile;
            }

            mode = "automático";
            foreach (var profile in _config.Profiles)
            {
                if (profile.Apps.Contains(app)) return profile;
            }

            return _config.General;
        }

        private void UpdateProfileIndicator(bool force = false)
        {
            var profile = GetActiveProfile(out string mode);
            string text = mode switch
            {
                "fijado" => $"{profile.Name} · fijado",
                "edición" => $"{profile.Name} · en edición",
                _ => $"{profile.Name} · automático"
            };

            if (!force && text == _lastProfileText) return;
            _lastProfileText = text;

            ProfilePillText.Text = text;
            ProfileIcon.Text = mode == "fijado" ? "\uE718" : "\uE77B";
            ProfilePill.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.45, 1, TimeSpan.FromMilliseconds(250)));

            UpdateTrayText();
        }

        private void UpdateTrayText()
        {
            if (_tray == null) return;

            string profileName = _lastProfileText?.Split('·')[0].Trim() ?? "General";
            string text = $"MacroPad · {profileName} · " + (IsMacroMode() ? "macro activo" : "modo normal");
            _tray.Text = text.Length > 63 ? text[..63] : text;   // límite de Windows para el texto de la bandeja
        }

        private void RebuildProfileMenu(System.Windows.Forms.ToolStripMenuItem root)
        {
            root.DropDownItems.Clear();

            var auto = new System.Windows.Forms.ToolStripMenuItem("Automático (según la ventana activa)")
            {
                Checked = _config.PinnedProfile == null
            };
            auto.Click += (_, _) => SetPinned(null);
            root.DropDownItems.Add(auto);
            root.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());

            foreach (var profile in _config.Profiles)
            {
                string name = profile.Name;
                var item = new System.Windows.Forms.ToolStripMenuItem("Fijar: " + name)
                {
                    Checked = _config.PinnedProfile == name
                };
                item.Click += (_, _) => SetPinned(name);
                root.DropDownItems.Add(item);
            }
        }

        // Fija un perfil a mano (name) o vuelve al cambio automático (null)
        private void SetPinned(string? name)
        {
            _config.PinnedProfile = name;
            if (!TrySaveConfig()) return;

            SyncPinCheck();
            UpdateProfileIndicator(force: true);
            ShowToast(name == null ? "Cambio automático de perfil activado" : $"Perfil fijado: {name}", ToastKind.Success);
        }

        private void SyncPinCheck()
        {
            _syncingProfileUi = true;
            PinCheck.IsChecked = _config.PinnedProfile != null && _config.PinnedProfile == _editProfile.Name;
            _syncingProfileUi = false;
        }

        private void PinCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingProfileUi) return;

            if (PinCheck.IsChecked == true) SetPinned(_editProfile.Name);
            else if (_config.PinnedProfile == _editProfile.Name) SetPinned(null);
        }

        // Vuelve a dibujar la lista de perfiles y sus detalles
        private void RefreshProfileUi()
        {
            _syncingProfileUi = true;
            ProfileList.ItemsSource = null;
            ProfileList.ItemsSource = _config.Profiles.ToList();
            ProfileList.SelectedItem = _editProfile;
            _syncingProfileUi = false;

            RefreshProfileDetails();
        }

        private void RefreshProfileDetails()
        {
            bool isGeneral = ReferenceEquals(_editProfile, _config.General);

            ProfileAppsBox.IsEnabled = !isGeneral;
            ProfileAppsPick.IsEnabled = !isGeneral;
            RenameProfileButton.IsEnabled = !isGeneral;
            DeleteProfileButton.IsEnabled = !isGeneral;

            ProfileAppsBox.Text = string.Join(", ", _editProfile.Apps);
            ProfileAppsHint.Text = isGeneral
                ? "General se usa cuando ningún otro perfil coincide con la ventana activa, y sus acciones sirven de respaldo."
                : "Nombres de programa separados por comas (por ejemplo: excel, winword). Usa «Programas abiertos» para no escribirlos. Las teclas sin acción en este perfil usan la de General.";

            SyncPinCheck();
        }

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_syncingProfileUi) return;
            if (ProfileList.SelectedItem is not ProfileData profile || ReferenceEquals(profile, _editProfile)) return;

            _editProfile = profile;
            RefreshProfileDetails();

            if (_selectedKey is Key key) SelectKey(key);
            else RefreshKeyboard();

            UpdateProfileIndicator();
        }

        private void ProfileAppsBox_LostFocus(object sender, RoutedEventArgs e) => ApplyProfileApps(ProfileAppsBox.Text);

        private void ProfileAppsPick_Click(object sender, RoutedEventArgs e) =>
            ShowPicker(app => ApplyProfileApps(ProfileAppsBox.Text + ", " + app.ProcessName));

        private void ApplyProfileApps(string text)
        {
            if (ReferenceEquals(_editProfile, _config.General)) return;

            var apps = ConfigManager.NormalizeApps(text.Split(',', ';', '\n'));
            ProfileAppsBox.Text = string.Join(", ", apps);

            if (apps.SequenceEqual(_editProfile.Apps)) return;

            _editProfile.Apps = apps;
            if (!TrySaveConfig()) return;

            AddLog($"Perfil «{_editProfile.Name}»: se activa con {(apps.Count == 0 ? "(ningún programa)" : string.Join(", ", apps))}");
            UpdateProfileIndicator(force: true);
        }

        private string? ValidateProfileName(string name, ProfileData? ignore)
        {
            if (name.Length == 0) return "Escribe un nombre para el perfil.";
            if (name.Length > 24) return "El nombre puede tener hasta 24 caracteres.";

            if (_config.Profiles.Any(p => !ReferenceEquals(p, ignore) && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                return "Ya existe un perfil con ese nombre.";

            return null;
        }

        private void NewProfileButton_Click(object sender, RoutedEventArgs e) =>
            ShowPrompt("Nuevo perfil", "Escribe un nombre, por ejemplo Excel o Diseño.", "", true, "Crear", name =>
            {
                string? error = ValidateProfileName(name, null);
                if (error != null) return error;

                var profile = new ProfileData { Name = name };
                _config.Profiles.Add(profile);
                if (!TrySaveConfig())
                {
                    _config.Profiles.Remove(profile);
                    return "No se pudo guardar el perfil.";
                }

                _editProfile = profile;
                RefreshProfileUi();
                if (_selectedKey is Key key) SelectKey(key); else RefreshKeyboard();
                UpdateProfileIndicator(force: true);

                ShowToast($"Perfil «{name}» creado. Indica con qué programas se activa.", ToastKind.Success);
                return null;
            });

        private void RenameProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(_editProfile, _config.General)) return;

            var profile = _editProfile;
            string oldName = profile.Name;

            ShowPrompt("Renombrar perfil", "", oldName, true, "Guardar", name =>
            {
                string? error = ValidateProfileName(name, profile);
                if (error != null) return error;

                bool wasPinned = _config.PinnedProfile == oldName;
                profile.Name = name;
                if (wasPinned) _config.PinnedProfile = name;
                TrySaveConfig();

                RefreshProfileUi();
                if (_selectedKey is Key key) SelectKey(key); else RefreshKeyboard();
                UpdateProfileIndicator(force: true);

                ShowToast("Perfil renombrado", ToastKind.Success);
                return null;
            });
        }

        private void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
        {
            if (ReferenceEquals(_editProfile, _config.General)) return;

            var profile = _editProfile;

            ShowPrompt("Eliminar perfil",
                $"Se eliminarán las acciones del perfil «{profile.Name}». La versión anterior queda en config.json.bak.",
                "", false, "Eliminar", _ =>
                {
                    _config.Profiles.Remove(profile);
                    if (_config.PinnedProfile == profile.Name) _config.PinnedProfile = null;
                    _editProfile = _config.General;
                    TrySaveConfig();

                    RefreshProfileUi();
                    if (_selectedKey is Key key) SelectKey(key); else RefreshKeyboard();
                    UpdateProfileIndicator(force: true);

                    ShowToast("Perfil eliminado", ToastKind.Success);
                    return null;
                });
        }

        // ----- Cuadro de nombre / confirmación -----

        private Func<string, string?>? _promptCallback;

        // onOk recibe el texto y devuelve un mensaje de error (el cuadro sigue abierto) o null (se cierra)
        private void ShowPrompt(string title, string message, string initial, bool needsText, string okText,
                                Func<string, string?> onOk)
        {
            _promptCallback = onOk;

            PromptTitle.Text = title;
            PromptMessage.Text = message;
            PromptMessage.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            PromptBox.Visibility = needsText ? Visibility.Visible : Visibility.Collapsed;
            PromptBox.Text = initial;
            PromptOk.Content = okText;

            PromptOverlay.Visibility = Visibility.Visible;
            PromptOverlay.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));

            if (needsText)
            {
                PromptBox.Focus();
                PromptBox.SelectAll();
            }
        }

        private void HidePrompt()
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
            fade.Completed += (_, _) => PromptOverlay.Visibility = Visibility.Collapsed;
            PromptOverlay.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        private void PromptOk_Click(object sender, RoutedEventArgs e)
        {
            string? error = _promptCallback?.Invoke(PromptBox.Text.Trim());
            if (error != null)
            {
                ShowToast(error, ToastKind.Warning);
                return;
            }

            HidePrompt();
        }

        private void PromptCancel_Click(object sender, RoutedEventArgs e) => HidePrompt();

        private void PromptBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;

            PromptOk_Click(sender, e);
            e.Handled = true;
        }

        // ---------- Exportar e importar la configuración ----------

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Exportar configuración",
                FileName = $"macropad-config-{DateTime.Now:yyyy-MM-dd}.json",
                Filter = "Configuración de MacroPad (*.json)|*.json"
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                ConfigManager.Export(dialog.FileName);
                AddLog("Configuración exportada a " + dialog.FileName);
                ShowToast("Configuración exportada", ToastKind.Success);
            }
            catch (Exception ex)
            {
                ShowToast("No se pudo exportar: " + ex.Message, ToastKind.Error);
            }
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Importar configuración",
                Filter = "Configuración de MacroPad (*.json)|*.json|Todos los archivos (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) != true) return;

            var warnings = new List<string>();
            var imported = ConfigManager.TryImport(dialog.FileName, warnings, out string? error);
            if (imported == null)
            {
                ShowToast(error ?? "No se pudo importar el archivo.", ToastKind.Error);
                return;
            }

            int keys = imported.Profiles.Sum(p => p.Bindings.Count);
            string ignored = warnings.Count > 0 ? $" Se ignoraron {warnings.Count} elemento(s) con problemas." : "";

            ShowPrompt("Importar configuración",
                $"El archivo tiene {imported.Profiles.Count} perfil(es) y {keys} teclas asignadas. " +
                $"Reemplazará tu configuración actual, que quedará en config.json.bak.{ignored}",
                "", false, "Importar", _ =>
                {
                    var previous = _config;
                    _config = imported;

                    if (!TrySaveConfig())
                    {
                        _config = previous;
                        return "No se pudo guardar la configuración importada.";
                    }

                    _editProfile = _config.General;

                    AddLog($"Configuración importada de {dialog.FileName}");
                    foreach (var w in warnings) AddLog("⚠ " + w);

                    RefreshProfileUi();
                    if (_selectedKey is Key key) SelectKey(key); else RefreshKeyboard();
                    UpdateProfileIndicator(force: true);

                    ShowToast("Configuración importada", ToastKind.Success);
                    return null;
                });
        }

        // ---------- Botones y casillas ----------

        private void ToggleButton_Click(object sender, RoutedEventArgs e) => ToggleMacroMode();

        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (LoadConfig())
                ShowToast("Configuración recargada", ToastKind.Success);
            else
                ShowToast("Hay avisos en config.json. Revisa el registro.", ToastKind.Warning);
        }

        private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigManager.ConfigFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ConfigManager.ConfigFolder}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AddLog("✖ no se pudo abrir la carpeta: " + ex.Message);
            }
        }

        private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_loadingUi) return;

            bool wanted = AutoStartCheck.IsChecked == true;
            try
            {
                AutoStart.SetEnabled(wanted);
                AddLog(wanted ? "Inicio con Windows ACTIVADO" : "Inicio con Windows DESACTIVADO");
                ShowToast(wanted ? "Inicio con Windows activado" : "Inicio con Windows desactivado", ToastKind.Success);
            }
            catch (Exception ex)
            {
                AddLog("✖ no se pudo cambiar el inicio automático: " + ex.Message);

                // Revertir la casilla sin volver a disparar el evento
                _loadingUi = true;
                AutoStartCheck.IsChecked = !wanted;
                _loadingUi = false;
            }
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

        // ---------- Utilidades ----------

        private void AddLog(string text)
        {
            _log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {text}");
            if (_log.Count > 200) _log.RemoveAt(_log.Count - 1);
        }

        private void RefreshDevices()
        {
            DeviceList.Items.Clear();
            foreach (var kb in _raw.GetKeyboards())
                DeviceList.Items.Add(kb.Name);
        }
    }
}
