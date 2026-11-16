using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Robot.Sensors;

namespace Robot.Dashboard.Wpf;

public sealed record LogEntry(string Time, string Kind, string Detail);

public sealed class DashboardViewModel : INotifyPropertyChanged
{
    public const string SimulationPort = "Simulation (no hardware)";
    private const int HistoryLength = 300;
    private const int LogLimit = 200;
    private readonly Queue<double?> rangeHistory = new();
    private readonly Queue<double?> lightHistory = new();
    private readonly Queue<double?> soundHistory = new();
    private SensorFrame? latest;
    private HelloFrame? hello;
    private bool simulated;
    private bool fresh;
    private bool connecting;
    private long frameCount;
    private long rejectCount;
    private double? meanSound;
    private string connection = "Disconnected";
    private string healthDetail = "Choose a port or simulation, then connect.";
    private string notice = "";

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Ports { get; } = [SimulationPort];
    public ObservableCollection<LogEntry> Log { get; } = [];
    public string SelectedPort { get; set; } = SimulationPort;
    public bool IsActive { get; private set; }
    public bool CanConnect => !IsActive;
    public bool CanDisconnect => IsActive;
    public bool IsFresh => fresh;
    public double ReadingOpacity => fresh ? 1 : 0.4;
    public string Connection => connection;
    public string HealthDetail => healthDetail;
    public string Notice => notice;
    public bool HasNotice => notice.Length > 0;
    public string StateLabel => !IsActive ? "DISCONNECTED" : !fresh ? (connecting ? "WAITING FOR DATA" : "STALE DATA") :
        simulated ? "SIMULATION" : "LIVE";
    public string StateColor => !fresh ? "#FDBA74" : simulated ? "#C4B5FD" : "#5EEAD4";
    public string ModeDescription => simulated
        ? "SIMULATION - synthetic values; no hardware is connected."
        : "Live sensor monitor only - no arm commands are sent.";
    public string DataDescription => !fresh
        ? "Readings are stale or unavailable. Charts leave gaps until valid telemetry resumes."
        : "Receiving telemetry. Charts show the last 30 seconds; display updates at 10 Hz.";
    public string Range => latest is null ? "--" : RangeFault ? "FAULT" : latest.RangeMm.ToString(CultureInfo.InvariantCulture);
    public string Light => latest?.Light.ToString(CultureInfo.InvariantCulture) ?? "--";
    public string Sound => latest?.Sound.ToString(CultureInfo.InvariantCulture) ?? "--";
    public string SoundMean => meanSound?.ToString("F1", CultureInfo.InvariantCulture) ?? "--";
    public string Red => ColorChannel(f => f.Red);
    public string Green => ColorChannel(f => f.Green);
    public string Blue => ColorChannel(f => f.Blue);
    public string Clear => latest is null ? "--" : ColorFault ? "FAULT" :
        hello?.Capabilities.HasFlag(SensorCapabilities.Clear) != true ? "N/A" :
        latest.Clear.ToString(CultureInfo.InvariantCulture);
    public string ColorDescription => hello is null ? "Waiting for channel capabilities" :
        hello.Capabilities.HasFlag(SensorCapabilities.Clear) ? "APDS9960 - raw RGBC counts" : "Raw RGB counts - clear not advertised";
    public bool RangeFault => latest?.Status.HasFlag(SensorStatus.RangeFault) == true;
    public bool ColorFault => latest?.Status.HasFlag(SensorStatus.ColorFault) == true;
    public string RangeColor => RangeFault ? "#FCA5A5" : "#5EEAD4";
    public string ColorStatus => ColorFault ? "COLOR SENSOR FAULT" : latest is null ? "WAITING" : "RAW COUNTS";
    public string Status => latest?.Status.ToString() ?? "No telemetry";
    public string Left => DigitalText(1);
    public string Center => DigitalText(2);
    public string Right => DigitalText(4);
    public string Touch => latest is null ? "--" : latest.Touch ? "TOUCHED" : "RELEASED";
    public string LeftColor => DigitalColor(1);
    public string CenterColor => DigitalColor(2);
    public string RightColor => DigitalColor(4);
    public string TouchColor => latest?.Touch == true ? "#C4B5FD" : "#94A3B8";
    public string Diagnostics => $"Frames {frameCount:N0}   |   Parse rejects {rejectCount:N0}   |   " +
        $"TX drops {hello?.TransmitDropped.ToString(CultureInfo.InvariantCulture) ?? "--"}   |   " +
        $"Device rejects {hello?.CommandsRejected.ToString(CultureInfo.InvariantCulture) ?? "--"}";
    public string DeviceDetails => $"Firmware {hello?.Firmware ?? "--"}   |   " +
        $"Max loop {hello?.MaxLoopMicroseconds.ToString(CultureInfo.InvariantCulture) ?? "--"} us   |   " +
        $"Reset {(hello is null ? "--" : hello.ResetCause == 255 ? "unknown" : hello.ResetCause.ToString(CultureInfo.InvariantCulture))}";
    public string Sequence => latest is null ? "Waiting for first frame" :
        $"Sequence {latest.Sequence:N0}   |   Device uptime {TimeSpan.FromMilliseconds(latest.Milliseconds):c}";
    public double LightLevel => latest?.Light ?? 0;
    public double SoundLevel => latest?.Sound ?? 0;
    public double?[] RangeHistory { get; private set; } = [];
    public double?[] LightHistory { get; private set; } = [];
    public double?[] SoundHistory { get; private set; } = [];

    public void Begin(bool simulation, string port)
    {
        latest = null;
        hello = null;
        simulated = simulation;
        fresh = false;
        connecting = true;
        frameCount = rejectCount = 0;
        meanSound = null;
        notice = "";
        IsActive = true;
        connection = simulation ? "Starting simulation" : $"Connecting to {port}";
        healthDetail = "Waiting for valid telemetry.";
        rangeHistory.Clear(); lightHistory.Clear(); soundHistory.Clear();
        RangeHistory = []; LightHistory = []; SoundHistory = [];
        Log.Clear();
        AddLog("SESSION", simulation ? "In-process fake Arduino selected." : $"Opening {port} at 115200 baud. DTR may reset the UNO.");
        Changed();
    }

    public void ApplyFrame(SensorFrame frame, long totalFrames)
    {
        latest = frame;
        frameCount = totalFrames;
        if (frame.Status.HasFlag(SensorStatus.Simulated)) simulated = true;
        Changed();
    }

    public void ApplyHello(HelloFrame frame)
    {
        hello = frame;
        Changed();
    }

    public void ApplyHealth(SensorHealth health)
    {
        rejectCount = health.RejectCount;
        switch (health.Kind)
        {
            case HealthKind.Connected:
                fresh = false; connecting = true; connection = "Connected - waiting";
                healthDetail = health.Detail;
                break;
            case HealthKind.Recovered:
                fresh = true; connecting = false;
                connection = simulated ? "Simulation running" : "Connected";
                healthDetail = health.Detail;
                break;
            case HealthKind.Disconnected:
                fresh = false; connecting = false; connection = "Reconnecting";
                healthDetail = health.Detail;
                break;
            case HealthKind.Silence:
                fresh = false; connecting = false; connection = "Telemetry silent";
                healthDetail = health.Detail;
                break;
            default:
                notice = $"{health.Kind}: {health.Detail}";
                break;
        }
        if (!fresh) meanSound = null;
        AddLog(health.Kind.ToString().ToUpperInvariant(), health.Detail, health.At);
        Changed();
    }

    public void ApplyEvent(SensorEvent evt) =>
        AddLog("INPUT", $"{evt.Source}: {(evt.Value ? "ACTIVE" : "released")} | seq {evt.Sequence} | {evt.Milliseconds} ms");

    public void ApplyMean(double value)
    {
        if (fresh) meanSound = value;
        Changed();
    }

    public void TickHistory()
    {
        Append(rangeHistory, fresh && latest is not null && !RangeFault ? latest.RangeMm : null);
        Append(lightHistory, fresh && latest is not null ? latest.Light : null);
        Append(soundHistory, fresh && latest is not null ? latest.Sound : null);
        RangeHistory = rangeHistory.ToArray();
        LightHistory = lightHistory.ToArray();
        SoundHistory = soundHistory.ToArray();
        PropertyChanged?.Invoke(this, new(nameof(RangeHistory)));
        PropertyChanged?.Invoke(this, new(nameof(LightHistory)));
        PropertyChanged?.Invoke(this, new(nameof(SoundHistory)));
    }

    public void End()
    {
        IsActive = fresh = connecting = false;
        meanSound = null;
        connection = "Disconnected";
        healthDetail = "Monitoring stopped. Last readings are retained but are not current.";
        AddLog("SESSION", "Disconnected; serial connection released.");
        Changed();
    }

    public void ReportError(Exception error)
    {
        notice = error.Message;
        AddLog("ERROR", error.Message);
        Changed();
    }

    public void ClearNotice() { notice = ""; Changed(); }

    private string ColorChannel(Func<SensorFrame, ushort> select) =>
        latest is null ? "--" : ColorFault ? "FAULT" : select(latest).ToString(CultureInfo.InvariantCulture);
    private string DigitalText(byte mask) => latest is null ? "--" : (latest.Infrared & mask) != 0 ? "DETECTED" : "CLEAR";
    private string DigitalColor(byte mask) => latest is not null && (latest.Infrared & mask) != 0 ? "#FDBA74" : "#94A3B8";
    private static void Append(Queue<double?> history, double? value)
    {
        history.Enqueue(value);
        if (history.Count > HistoryLength) history.Dequeue();
    }
    private void AddLog(string kind, string detail, DateTimeOffset? at = null)
    {
        Log.Insert(0, new((at ?? DateTimeOffset.Now).ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture), kind, detail));
        if (Log.Count > LogLimit) Log.RemoveAt(Log.Count - 1);
    }
    private void Changed() => PropertyChanged?.Invoke(this, new(string.Empty));
}
