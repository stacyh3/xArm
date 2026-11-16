using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace Robot.Dashboard.Wpf.Tests;

public sealed class WindowTests
{
    [Fact]
    public void FakeWindowRendersUpdatesAndCanDisconnectAndReconnect()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            var bindingErrors = new StringWriter();
            var listener = new TextWriterTraceListener(bindingErrors);
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            try
            {
                window = new MainWindow("--fake") { Width = 1180, Height = 1000 };
                window.Show();
                PumpFor(TimeSpan.FromSeconds(2));
                var model = Assert.IsType<DashboardViewModel>(window.DataContext);
                Assert.Equal("SIMULATION", ((TextBlock)window.FindName("StateLabel")).Text);
                Assert.Equal("250", ((TextBlock)window.FindName("RangeValue")).Text);
                Assert.True(model.RangeHistory.Length >= 5);
                Assert.Contains(model.RangeHistory, value => value == 250);
                Assert.NotEqual("--", model.SoundMean);
                Assert.Empty(bindingErrors.ToString());
                string? screenshot = Environment.GetEnvironmentVariable("XARM_WPF_SCREENSHOT");
                if (!string.IsNullOrEmpty(screenshot))
                {
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(screenshot);
                    encoder.Save(file);
                }
                ((Button)window.FindName("DisconnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(model.IsActive);
                Assert.Equal("DISCONNECTED", ((TextBlock)window.FindName("StateLabel")).Text);
                int historyCount = model.RangeHistory.Length;
                PumpFor(TimeSpan.FromMilliseconds(300));
                Assert.Equal(historyCount, model.RangeHistory.Length);
                ((Button)window.FindName("ConnectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpFor(TimeSpan.FromSeconds(1));
                Assert.True(model.IsFresh);
                Assert.Equal("SIMULATION", model.StateLabel);
                Assert.Empty(bindingErrors.ToString());
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                window?.Close();
                PresentationTraceSources.DataBindingSource.Listeners.Remove(listener);
                listener.Dispose();
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF UI thread did not finish its smoke test.");
        Assert.Null(failure);
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
