using System;
using System.Windows;

namespace AudioRecorder
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Set up global exception handling
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // Show the main window
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            string errorMessage = $"An unexpected error occurred:\n\n{e.Exception.Message}";
            
            if (e.Exception.InnerException != null)
            {
                errorMessage += $"\n\nInner Exception: {e.Exception.InnerException.Message}";
            }

            // Add stack trace for debugging (optional)
            #if DEBUG
            errorMessage += $"\n\nStack Trace:\n{e.Exception.StackTrace}";
            #endif

            MessageBox.Show(errorMessage, "Audio Recorder Error", MessageBoxButton.OK, MessageBoxImage.Error);
            
            // Log to debug output
            System.Diagnostics.Debug.WriteLine($"Unhandled UI Exception: {e.Exception}");
            
            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            string errorMessage = $"A critical error occurred:\n\n{exception?.Message ?? "Unknown error"}";
            
            if (exception?.InnerException != null)
            {
                errorMessage += $"\n\nInner Exception: {exception.InnerException.Message}";
            }

            // Log to debug output
            System.Diagnostics.Debug.WriteLine($"Unhandled Critical Exception: {exception}");

            MessageBox.Show(errorMessage, "Audio Recorder Critical Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
