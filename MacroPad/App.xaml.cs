using System;
using System.Threading;

namespace MacroPad
{
    public partial class App : System.Windows.Application
    {
        private const string MutexName = @"Local\MacroPad.SingleInstance";
        private const string ShowEventName = @"Local\MacroPad.ShowWindow";

        private Mutex? _mutex;
        private EventWaitHandle? _showEvent;
        private bool _ownsMutex;

        protected override void OnStartup(System.Windows.StartupEventArgs e)
        {
            base.OnStartup(e);

            _mutex = new Mutex(true, MutexName, out bool isFirstInstance);

            if (!isFirstInstance)
            {
                // Ya hay una copia en ejecución: le pedimos que muestre su ventana y esta se cierra
                try
                {
                    using var existing = EventWaitHandle.OpenExisting(ShowEventName);
                    existing.Set();
                }
                catch { /* si la otra copia aún no terminó de arrancar, no pasa nada */ }

                Shutdown();
                return;
            }

            _ownsMutex = true;

            // Hilo que espera la señal "muestra tu ventana" de una segunda copia
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            new Thread(WaitForShowRequests) { IsBackground = true }.Start();

            new MainWindow().Show();
        }

        private void WaitForShowRequests()
        {
            try
            {
                while (_showEvent != null && _showEvent.WaitOne())
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (Current.MainWindow is MainWindow window)
                            window.ShowFromTray();
                    });
                }
            }
            catch (ObjectDisposedException) { /* la aplicación se está cerrando */ }
        }

        protected override void OnExit(System.Windows.ExitEventArgs e)
        {
            _showEvent?.Dispose();

            if (_ownsMutex)
            {
                try { _mutex?.ReleaseMutex(); } catch { }
            }
            _mutex?.Dispose();

            base.OnExit(e);
        }
    }
}
