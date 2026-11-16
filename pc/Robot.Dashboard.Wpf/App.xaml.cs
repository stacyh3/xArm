using System.Windows;

namespace Robot.Dashboard.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 1 || (e.Args.Length == 1 && e.Args[0].StartsWith("--", StringComparison.Ordinal) && e.Args[0] != "--fake"))
        {
            MessageBox.Show("Usage: Robot.Dashboard.Wpf [--fake|COM-port]", "Invalid arguments",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        MainWindow = new MainWindow(e.Args.FirstOrDefault());
        MainWindow.Show();
    }
}
