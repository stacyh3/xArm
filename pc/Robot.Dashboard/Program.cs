using System.IO.Ports;
using System.Reactive.Linq;
using Robot.Sensors;

if (args is ["--ports"])
{
    Console.WriteLine(string.Join(Environment.NewLine, SerialPort.GetPortNames()));
    return;
}
if (args.Length != 1 && args is not ["--fake-arduino", _])
{
    Console.Error.WriteLine("Usage: Robot.Dashboard <COM-port|--fake|--ports> or --fake-arduino <COM-port>");
    Environment.ExitCode = 2;
    return;
}
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
try
{
    if (args is ["--fake-arduino", var fakePort])
    {
        Console.WriteLine($"Fake Arduino on {fakePort}; connect the dashboard to the other end of the virtual serial pair.");
        await FakeArduino.RunSerialAsync(fakePort, stop.Token);
        return;
    }
    bool fake = args[0] == "--fake";
    var gateway = new SensorGateway(() => fake ? new FakeConnection() : new SerialConnection(args[0]));
    object gate = new();
    SensorFrame? latest = null;
    HelloFrame? hello = null;
    SensorHealth? health = null;
    SensorEvent? lastEvent = null;
    double meanSound = 0;
    long frames = 0, changes = 0;
    bool terminal = !Console.IsOutputRedirected;
    void Fail(Exception ex)
    {
        Console.Error.WriteLine(ex);
        Environment.ExitCode = 1;
        stop.Cancel();
    }
    void Render()
    {
        lock (gate)
        {
            string title = fake ? "xArm sensors - SIMULATION (no hardware)" : $"xArm sensors - {args[0]}";
            string text = $"{title}\nHealth: {health?.Kind}: {health?.Detail}\n" +
                $"Firmware: {hello?.Firmware ?? "waiting"} | rejects: {health?.RejectCount ?? 0} " +
                $"| device drops: {hello?.TransmitDropped ?? 0} | max loop: {hello?.MaxLoopMicroseconds ?? 0} us\n";
            if (latest is { } f)
            {
                bool clearAvailable = hello?.Capabilities.HasFlag(SensorCapabilities.Clear) == true;
                text += $"Frames: {frames} | changed values: {changes} | seq: {f.Sequence} | uptime: {f.Milliseconds} ms\n" +
                    (health?.Kind is HealthKind.Disconnected or HealthKind.Silence or HealthKind.Connected
                        ? "STALE: displayed values are the last received readings, not current measurements.\n" : "") +
                    $"STATUS: {f.Status}\n" +
                    $"Range: {(f.Status.HasFlag(SensorStatus.RangeFault) ? "FAULT" : $"{f.RangeMm} mm")}\n" +
                    $"IR left/center/right: {(f.Infrared & 1) != 0}/{(f.Infrared & 2) != 0}/{(f.Infrared & 4) != 0} | Touch: {f.Touch}\n" +
                    $"Light: {f.Light}/1023 | Sound: {f.Sound}/1023 | sound mean (1s): {meanSound:F1}\n" +
                    $"Color: {(f.Status.HasFlag(SensorStatus.ColorFault) ? "FAULT" : $"R {f.Red} G {f.Green} B {f.Blue} Clear {(clearAvailable ? f.Clear.ToString() : "N/A")}")}\n";
            }
            text += $"Last event: {lastEvent?.Source ?? "-"} {lastEvent?.Value}\nCtrl+C to exit.\n";
            if (terminal)
            {
                Console.Write("\u001b[H\u001b[2J");
                Console.Write(text);
            }
            else Console.WriteLine(text);
        }
    }
    // Keep an explicit connection subscription alive while derived views attach.
    using var updates = gateway.Updates.Subscribe(update =>
    {
        lock (gate)
        {
            if (update.Health is { } h) health = h;
            if (update.Message is HelloFrame hFrame) hello = hFrame;
            if (update.Message is SensorEvent evt) lastEvent = evt;
            if (update.Message is SensorFrame frame) { latest = frame; frames++; }
        }
    }, Fail);
    using var changed = gateway.Frames
        .Select(f => (f.RangeMm, f.Infrared, f.Touch, f.Light, f.Sound, f.Red, f.Green, f.Blue, f.Clear, f.Status))
        .DistinctUntilChanged()
        .Subscribe(_ => { lock (gate) changes++; }, Fail);
    using var aggregate = gateway.Frames.Buffer(TimeSpan.FromSeconds(1))
        .Where(batch => batch.Count > 0)
        .Select(batch => batch.Average(f => (double)f.Sound))
        .Subscribe(mean => { lock (gate) meanSound = mean; }, Fail);
    using var refresh = Observable.Interval(TimeSpan.FromMilliseconds(250)).Subscribe(_ => Render(), Fail);
    await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
}
