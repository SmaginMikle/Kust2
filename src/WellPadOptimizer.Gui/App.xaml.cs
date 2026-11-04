using System.Windows;

namespace WellPadOptimizer.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show(ex.Exception.Message + "\n\n" + ex.Exception.StackTrace, "WellPadOptimizer — ошибка",
                MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        base.OnStartup(e);
    }
}
