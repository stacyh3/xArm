using System.IO;
using System.IO.Ports;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows;
using System.Windows.Threading;
using Robot.Sensors;

namespace Robot.Dashboard.Wpf;

public partial class MainWindow : Window
{
    private readonly DashboardViewModel model = new();
    private readonly string? initialSource;
    private CompositeDisposable? session;
    private int generation;

    public MainWindow(string? initialSource = null)
    {
        InitializeComponent();
        DataContext = model;
        this.initialSource = initialSource;
    }

    private void WindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshPorts();
        if (initialSource is null) return;
        model.SelectedPort = initialSource == "--fake" ? DashboardViewModel.SimulationPort : initialSource;
        if (!model.Ports.Contains(model.SelectedPort)) model.Ports.Add(model.SelectedPort);
        PortPicker.SelectedItem = model.SelectedPort;
        StartSession();
    }

    private void RefreshPorts()
    {
        string selected = model.SelectedPort;
        try
        {
            string[] ports = SerialPort.GetPortNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            model.Ports.Clear();
            model.Ports.Add(DashboardViewModel.SimulationPort);
            foreach (string port in ports) model.Ports.Add(port);
            model.SelectedPort = model.Ports.Contains(selected) ? selected : DashboardViewModel.SimulationPort;
            PortPicker.SelectedItem = model.SelectedPort;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            model.ReportError(ex);
        }
    }

    private void StartSession()
    {
        if (session is not null) return;
        string source = model.SelectedPort;
        bool fake = source == DashboardViewModel.SimulationPort;
        int current = ++generation;
        long count = 0;
        model.Begin(fake, source);
        session = new CompositeDisposable();
        var ui = new SynchronizationContextScheduler(new DispatcherSynchronizationContext(Dispatcher));
        var gateway = new SensorGateway(() => fake ? new FakeConnection() : new SerialConnection(source));
        var updates = gateway.Updates.Publish();
        var frames = updates.Where(u => u.Message is SensorFrame).Select(u => (SensorFrame)u.Message!)
            .Do(_ => Interlocked.Increment(ref count)).Publish();

        void Fail(Exception error)
        {
            if (current != generation) return;
            StopSession();
            model.ReportError(error);
        }
        session.Add(updates.Where(u => u.Message is not SensorFrame).ObserveOn(ui).Subscribe(update =>
        {
            if (current != generation) return;
            if (update.Health is { } health) model.ApplyHealth(health);
            if (update.Message is HelloFrame hello) model.ApplyHello(hello);
            if (update.Message is SensorEvent evt) model.ApplyEvent(evt);
        }, Fail));
        session.Add(frames.Sample(TimeSpan.FromMilliseconds(100)).ObserveOn(ui).Subscribe(frame =>
        {
            if (current == generation) model.ApplyFrame(frame, Interlocked.Read(ref count));
        }, Fail));
        session.Add(frames.Buffer(TimeSpan.FromSeconds(1)).Where(batch => batch.Count > 0)
            .Select(batch => batch.Average(f => (double)f.Sound)).ObserveOn(ui).Subscribe(mean =>
            {
                if (current == generation) model.ApplyMean(mean);
            }, Fail));
        session.Add(Observable.Interval(TimeSpan.FromMilliseconds(100)).ObserveOn(ui).Subscribe(_ =>
        {
            if (current == generation) model.TickHistory();
        }, Fail));
        session.Add(frames.Connect());
        session.Add(updates.Connect());
    }

    private void StopSession()
    {
        ++generation;
        session?.Dispose();
        session = null;
        model.End();
    }

    private void RefreshPortsClicked(object sender, RoutedEventArgs e) => RefreshPorts();
    private void ConnectClicked(object sender, RoutedEventArgs e) => StartSession();
    private void DisconnectClicked(object sender, RoutedEventArgs e) => StopSession();
    private void DismissClicked(object sender, RoutedEventArgs e) => model.ClearNotice();
    private void WindowClosed(object? sender, EventArgs e) => StopSession();
}
