<Query Kind = "Program">
  <NuGetReference>HidSharp</NuGetReference>
  <NuGetReference>System.IO.Ports</NuGetReference>
  <Namespace>HidSharp</Namespace>
  <Namespace>System.IO.Ports</Namespace>
  <Namespace>System.Windows</Namespace>
  <Namespace>System.Windows.Controls</Namespace>
  <Namespace>System.Windows.Media</Namespace>
  <Namespace>System.Windows.Threading</Namespace>
</Query>

// xArm / LSC controller tester. WPF built in code (no XAML).
// Protocol (LSC Series Servo Controller V1.2): 0x55 0x55 <len> <cmd> <params...>, len = params + 2.
// Transport: USB HID (VID 0483, PID 5750) or TTL serial at 9600 baud.
// SAFETY: keep the arm clear. Connect, press "Read" to sync sliders, then move with long times first. "Home" moves every joint.

// LINQPad.Controls also defines Button/TextBox/CheckBox, so qualify those three.
using W = System.Windows.Controls;

const byte CmdMove = 3, CmdBattery = 0x0F, CmdUnload = 0x14, CmdPosRead = 0x15;
const int ServoCount = 6;   // joint names and per-joint limits live in the Joint enum and Joints class at the bottom

void Main()
{
	var ui = Dispatcher.CurrentDispatcher;
	IArmLink link = null;
	var parser = new FrameParser();
	bool updating = false, dirty = false;

	// ---- controls ----
	var log = new W.TextBox
	{
		IsReadOnly = true,
		Height = 170,
		FontFamily = new FontFamily("Consolas"),
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		TextWrapping = TextWrapping.Wrap
	};
	var mode = new ComboBox { ItemsSource = new[] { "USB HID", "Serial (COM)" }, SelectedIndex = 0, Width = 110 };
	var port = new W.TextBox { Text = "COM3", Width = 60, Margin = new Thickness(6, 0, 6, 0) };
	var connect = Btn("Connect");
	var timeBox = new W.TextBox { Text = "500", Width = 60, Margin = new Thickness(6, 0, 0, 0) };
	var live = new W.CheckBox { Content = "Live (send while dragging)", Margin = new Thickness(0, 8, 0, 8) };
	var sliders = new Slider[ServoCount];
	var vals = new TextBlock[ServoCount];

	void Log(string s) => ui.InvokeAsync(() => { log.AppendText(s + "\n"); log.ScrollToEnd(); });

	bool Tx(byte[] f)
	{
		if (link == null) { Log("Not connected"); return false; }
		try { link.Send(f); Log("TX " + BitConverter.ToString(f)); return true; }
		catch (Exception e) { Log("TX error: " + e.Message); return false; }
	}

	int Ms() => int.TryParse(timeBox.Text, out var t) ? Math.Clamp(t, 0, 30000) : 500;
	(Joint joint, int pos)[] Positions() => Joints.All.Select((j, i) => (j.Joint, (int)sliders[i].Value)).ToArray();
	void SendMove() { dirty = false; Tx(Frames.MoveAll(Positions(), Ms())); }

	// ---- receive ----
	void OnRx(byte[] data)
	{
		foreach (var (cmd, p) in parser.Feed(data))
		{
			Log($"RX cmd {cmd}: {BitConverter.ToString(p)}");
			if (cmd == CmdBattery && p.Length >= 2) Log($"Battery: {p[0] | p[1] << 8} mV");
			else if (cmd == CmdPosRead && p.Length >= 1)
			{
				var pos = new Dictionary<int, int>();
				for (int i = 1; i + 2 < p.Length; i += 3) pos[p[i]] = (short)(p[i + 1] | p[i + 2] << 8);
				Log("Positions: " + string.Join(", ", pos.Select(kv => $"{Joints.Name(kv.Key)}={kv.Value}")));
				ui.InvokeAsync(() =>
				{
					updating = true;
					for (int i = 0; i < ServoCount; i++)
						if (pos.TryGetValue((int)Joints.All[i].Joint, out var v))
							sliders[i].Value = Math.Clamp(v, Joints.All[i].Min, Joints.All[i].Max);
					updating = false;
				});
			}
		}
	}

	// ---- layout ----
	var root = new StackPanel { Margin = new Thickness(12) };
	var conn = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
	conn.Children.Add(mode); conn.Children.Add(port); conn.Children.Add(connect);
	root.Children.Add(conn);

	for (int i = 0; i < ServoCount; i++)
	{
		int idx = i;
		var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
		var j = Joints.All[i];
		sliders[i] = new Slider { Minimum = j.Min, Maximum = j.Max, Value = j.Home, Width = 260 };
		vals[i] = new TextBlock { Text = j.Home.ToString(), Width = 40, Margin = new Thickness(8, 0, 0, 0) };
		sliders[i].ValueChanged += (s, e) =>
		{
			vals[idx].Text = ((int)e.NewValue).ToString();
			if (!updating && live.IsChecked == true) dirty = true;
		};
		row.Children.Add(new TextBlock { Text = $"{j.Label} ({(byte)j.Joint})", Width = 120 });
		row.Children.Add(sliders[i]); row.Children.Add(vals[i]);
		root.Children.Add(row);
	}

	var timeRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
	timeRow.Children.Add(new TextBlock { Text = "Move time (ms):" }); timeRow.Children.Add(timeBox);
	root.Children.Add(timeRow); root.Children.Add(live);

	var buttons = new WrapPanel();
	var send = Btn("Send"); var center = Btn("Home"); var read = Btn("Read positions");
	var battery = Btn("Battery"); var release = Btn("Release servos");
	foreach (var b in new[] { send, center, read, battery, release }) buttons.Children.Add(b);
	root.Children.Add(buttons);
	root.Children.Add(new TextBlock { Text = "Log", Margin = new Thickness(0, 10, 0, 2) });
	root.Children.Add(log);

	// ---- behavior ----
	connect.Click += (s, e) =>
	{
		if (link != null) { link.Dispose(); link = null; connect.Content = "Connect"; Log("Disconnected"); return; }
		try
		{
			if (mode.SelectedIndex == 0) link = new HidLink(); else link = new SerialLink(port.Text, 9600);
			link.Received += OnRx;
			connect.Content = "Disconnect"; Log("Connected");
		}
		catch (Exception ex) { Log("Connect failed: " + ex.Message); }
	};
	send.Click += (s, e) => SendMove();
	center.Click += (s, e) => { updating = true; for (int i = 0; i < ServoCount; i++) sliders[i].Value = Joints.All[i].Home; updating = false; SendMove(); };
	read.Click += (s, e) => Tx(Frames.Make(CmdPosRead, ServoIds()));
	battery.Click += (s, e) => Tx(Frames.Make(CmdBattery));
	release.Click += (s, e) => Tx(Frames.Make(CmdUnload, ServoIds()));

	var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
	timer.Tick += (s, e) => { if (dirty && live.IsChecked == true) SendMove(); };
	timer.Start();

	var win = new Window { Title = "xArm control", Width = 520, SizeToContent = SizeToContent.Height, Content = root };
	win.Closed += (s, e) => { timer.Stop(); link?.Dispose(); };
	win.ShowDialog();
}

W.Button Btn(string text) => new W.Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6) };

// [count, id, id, ...] for every joint
byte[] ServoIds() => new[] { (byte)ServoCount }.Concat(Joints.All.Select(j => (byte)j.Joint)).ToArray();

// Servo IDs in the controller's numbering. Confirm by moving one slider at a time.
enum Joint : byte { Gripper = 1, WristRoll = 2, WristPitch = 3, Elbow = 4, Shoulder = 5, Base = 6 }

record JointInfo(Joint Joint, string Label, int Min, int Max, int Home);

static class Joints
{
	// Min/Max/Home are conservative PLACEHOLDERS in servo units (0-1000). Tune each joint on the real arm.
	public static readonly JointInfo[] All =
	{
		new(Joint.Gripper,    "Gripper",     300, 700, 500),
		new(Joint.WristRoll,  "Wrist roll",  100, 900, 500),
		new(Joint.WristPitch, "Wrist pitch", 200, 800, 500),
		new(Joint.Elbow,      "Elbow",       150, 850, 500),
		new(Joint.Shoulder,   "Shoulder",    250, 750, 500),
		new(Joint.Base,       "Base",        100, 900, 500),
	};

	public static string Name(int id) => Enum.IsDefined(typeof(Joint), (byte)id) ? ((Joint)(byte)id).ToString() : $"Servo{id}";
}

static class Frames
{
	public static byte[] Make(byte cmd, params byte[] prm)
	{
		var f = new byte[4 + prm.Length];
		f[0] = f[1] = 0x55; f[2] = (byte)(prm.Length + 2); f[3] = cmd;
		prm.CopyTo(f, 4);
		return f;
	}

	// CMD_SERVO_MOVE: count, time lo/hi, then (id, pos lo/hi) per servo. Length = servos*3 + 5.
	public static byte[] MoveAll((Joint joint, int pos)[] targets, int ms)
	{
		var p = new List<byte> { (byte)targets.Length, (byte)(ms & 0xFF), (byte)(ms >> 8) };
		foreach (var (joint, pos) in targets) { p.Add((byte)joint); p.Add((byte)(pos & 0xFF)); p.Add((byte)(pos >> 8)); }
		return Make(3, p.ToArray());
	}
}

// Reassembles frames from arbitrary byte chunks. HID reports arrive zero-padded; the scan for 55 55 skips the padding.
class FrameParser
{
	readonly List<byte> buf = new();

	public IEnumerable<(byte cmd, byte[] prm)> Feed(byte[] data)
	{
		buf.AddRange(data);
		while (true)
		{
			int i = -1;
			for (int k = 0; k + 1 < buf.Count; k++)
			{
				if (buf[k] == 0x55 && buf[k + 1] == 0x55)
				{
					i = k; break;
				}
			}

			if (i < 0)
			{
				if (buf.Count > 1) buf.RemoveRange(0, buf.Count - 1); yield break;
			}

			if (i > 0)
				buf.RemoveRange(0, i);

			if (buf.Count < 3)
				yield break;

			int len = buf[2];

			if (len < 2)
			{
				buf.RemoveRange(0, 2); continue;
			}

			if (buf.Count < len + 2)
				yield break;

			var cmd = buf[3];
			var prm = buf.Skip(4).Take(len - 2).ToArray();
			buf.RemoveRange(0, len + 2);

			yield return (cmd, prm);
		}
	}
}

interface IArmLink : IDisposable
{
	void Send(byte[] frame);
	event Action<byte[]> Received;
}

class HidLink : IArmLink
{
	readonly HidDevice dev; readonly HidStream stream;
	public event Action<byte[]> Received;

	public HidLink()
	{
		dev = DeviceList.Local.GetHidDeviceOrNull(0x0483, 0x5750)
			?? throw new Exception("xArm HID device (0483:5750) not found");
		stream = dev.Open();
		stream.ReadTimeout = Timeout.Infinite;
		var buf = new byte[dev.GetMaxInputReportLength()];
		new Thread(() =>
		{
			try { while (true) { int n = stream.Read(buf); if (n > 1) Received?.Invoke(buf.Skip(1).Take(n - 1).ToArray()); } }
			catch { /* stream closed */ }
		})
		{ IsBackground = true }.Start();
	}

	public void Send(byte[] frame)
	{
		var report = new byte[dev.GetMaxOutputReportLength()];   // [0] = report ID 0, zero padded
		Array.Copy(frame, 0, report, 1, frame.Length);
		stream.Write(report);
	}

	public void Dispose() => stream.Dispose();
}

class SerialLink : IArmLink
{
	readonly SerialPort port;
	public event Action<byte[]> Received;

	public SerialLink(string name, int baud)
	{
		port = new SerialPort(name, baud);
		port.Open();
		port.DataReceived += (s, e) =>
		{
			var b = new byte[port.BytesToRead];
			port.Read(b, 0, b.Length);
			Received?.Invoke(b);
		};
	}

	public void Send(byte[] frame) => port.Write(frame, 0, frame.Length);
	public void Dispose() => port.Dispose();
}
