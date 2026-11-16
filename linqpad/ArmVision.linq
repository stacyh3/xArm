<Query Kind="Program">
  <NuGetReference Version="4.9.0.20240103">OpenCvSharp4</NuGetReference>
  <NuGetReference Version="4.9.0.20240103">OpenCvSharp4.runtime.win</NuGetReference>
  <NuGetReference Version="4.9.0.20240103">OpenCvSharp4.WpfExtensions</NuGetReference>
  <NuGetReference>Microsoft.ML.OnnxRuntime</NuGetReference>
  <Namespace>OpenCvSharp</Namespace>
  <Namespace>OpenCvSharp.Aruco</Namespace>
  <Namespace>OpenCvSharp.Dnn</Namespace>
  <Namespace>OpenCvSharp.WpfExtensions</Namespace>
  <Namespace>System.Net</Namespace>
  <Namespace>System.Text.Json</Namespace>
  <Namespace>System.Windows</Namespace>
  <Namespace>System.Windows.Controls</Namespace>
  <Namespace>System.Windows.Media</Namespace>
  <Namespace>System.Windows.Threading</Namespace>
</Query>

using Microsoft.ML.OnnxRuntime;

// Vision prototype for the arm: webcam -> YOLO (ONNX Runtime) -> labelled objects with table coordinates in mm.
//
// SETUP
//  1. Export a model once:  pip install ultralytics
//                           yolo export model=yolo11n.pt format=onnx opset=12 simplify=True imgsz=640
//     then set ModelPath below. (OpenCvSharp is pinned to 4.9 because its ArUco API names changed in later versions.)
//  2. Print aruco_markers_4x4_ids0-7.pdf at 100% (DICT_4X4_50, 40 mm markers), lay them flat in the workspace,
//     measure each marker center in mm from the arm base and fill in MarkerMm below.
//  3. Run, check the markers are outlined, press "Calibrate from markers". The result is saved next to this script.
//  4. Latest objects as JSON: http://localhost:5055/objects (also shown in the window, with a Copy button).
// Close the window or stop the query to release the camera. Shutdown waits for capture/inference to finish.
//
// Table frame: origin under the arm base axis, +X forward from the base, +Y to the left, viewed from above.
// Object position = center of the detection box, which assumes a top-down camera and objects of low height.

// LINQPad.Controls also defines Button/TextBox/Image, and OpenCvSharp defines Point/Size/Rect/Window; alias to avoid clashes.
using W = System.Windows.Controls;
using CvRect = OpenCvSharp.Rect;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;
using WinWindow = System.Windows.Window;

// ---------- settings ----------
//TODO: Improve vision system.
static readonly string ModelPath = @"C:\repos.demos\yolo\yolo11n.onnx";
static readonly string NamesPath = "";          // optional classes.txt for a custom model; empty = COCO names
static readonly int CameraIndex = 2, InputSize = 640, HttpPort = 5055;

// ArUco centers in the arm base frame, millimeters. PLACEHOLDERS: measure yours. 4 markers minimum, 6+ gives a meaningful error figure.
static readonly Dictionary<int, (double x, double y)> MarkerMm = new()
{
	[0] = (100, 150),
	[1] = (100, -150),
	[2] = (300, -150),
	[3] = (300, 150),
};

// ---------- shared state ----------
static readonly object Gate = new();
static Mat H;                                    // pixel -> mm homography (3x3, CV_64F)
static double calibRmsMm = double.NaN;
static volatile string latestJson = "{}";
static volatile bool CalibrateRequested = false, UiPending = false;
static volatile float ConfThr = 0.4f;
static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
static readonly Scalar Green = new(0, 255, 0), Amber = new(0, 165, 255);

void Main()
{
	var ui = Dispatcher.CurrentDispatcher;
	var names = NamesPath != "" && File.Exists(NamesPath) ? File.ReadAllLines(NamesPath) : Coco;
	if (!File.Exists(ModelPath))
		throw new FileNotFoundException("YOLO ONNX model not found.", ModelPath);
	using var stop = new CancellationTokenSource();
	CalibrateRequested = UiPending = false;
	latestJson = "{}";

	// ---- UI ----
	var img = new W.Image { Stretch = Stretch.Uniform };
	var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
	var json = new W.TextBox
	{
		IsReadOnly = true,
		FontFamily = new FontFamily("Consolas"),
		FontSize = 11,
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
		TextWrapping = TextWrapping.Wrap,
		Height = 380
	};
	var conf = new Slider { Minimum = 0.1, Maximum = 0.9, Value = ConfThr };
	var confLabel = new TextBlock { Text = $"Confidence threshold: {ConfThr:F2}" };
	conf.ValueChanged += (s, e) => { ConfThr = (float)e.NewValue; confLabel.Text = $"Confidence threshold: {ConfThr:F2}"; };
	var calibrate = new W.Button { Content = "Calibrate from markers", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 6, 6) };
	var copy = new W.Button { Content = "Copy JSON", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 0, 6) };
	calibrate.Click += (s, e) => CalibrateRequested = true;
	copy.Click += (s, e) => Clipboard.SetText(latestJson);

	var side = new StackPanel { Width = 380, Margin = new Thickness(10) };
	var btns = new StackPanel { Orientation = Orientation.Horizontal };
	btns.Children.Add(calibrate); btns.Children.Add(copy);
	side.Children.Add(confLabel); side.Children.Add(conf); side.Children.Add(btns);
	side.Children.Add(status);
	side.Children.Add(new TextBlock { Text = $"http://localhost:{HttpPort}/objects", FontFamily = new FontFamily("Consolas") });
	side.Children.Add(json);
	var root = new DockPanel();
	DockPanel.SetDock(side, Dock.Right);
	root.Children.Add(side); root.Children.Add(img);

	// ---- JSON endpoint ----
	var http = new HttpListener();
	Thread httpWorker = null;
	try
	{
		http.Prefixes.Add($"http://localhost:{HttpPort}/");
		http.Start();
		httpWorker = new Thread(() =>
		{
			while (!stop.IsCancellationRequested)
			{
				try
				{
					var ctx = http.GetContext();
					var bytes = Encoding.UTF8.GetBytes(latestJson);
					ctx.Response.ContentType = "application/json";
					ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
					ctx.Response.Close();
				}
				catch (HttpListenerException) when (stop.IsCancellationRequested) { break; }
				catch (ObjectDisposedException) when (stop.IsCancellationRequested) { break; }
				catch (Exception ex)
				{
					Console.Error.WriteLine("HTTP endpoint failed: " + ex);
					ui.InvokeAsync(() => { if (!stop.IsCancellationRequested) status.Text = "HTTP endpoint failed: " + ex.Message; });
					break;
				}
			}
		})
		{ IsBackground = true, Name = "ArmVision HTTP" };
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine("HTTP endpoint failed: " + ex);
		status.Text = "HTTP endpoint failed: " + ex.Message;
	}

	// ---- capture + vision loop ----
	string note = "";
	var worker = new Thread(() =>
	{
		try
		{
			using var net = new InferenceSession(ModelPath);
			using var dict = CvAruco.GetPredefinedDictionary(PredefinedDictionaryName.Dict4X4_50);
			var prm = new DetectorParameters();
			LoadCalibration();
			using var cap = new VideoCapture(CameraIndex, VideoCaptureAPIs.DSHOW);
			if (!cap.IsOpened()) throw new InvalidOperationException("Could not open camera " + CameraIndex);
			cap.Set(VideoCaptureProperties.FrameWidth, 1280);
			cap.Set(VideoCaptureProperties.FrameHeight, 720);
			cap.Set(VideoCaptureProperties.FourCC, VideoWriter.FourCC('M', 'J', 'P', 'G'));
			using var frame = new Mat();
			var sw = Stopwatch.StartNew(); int count = 0; double fps = 0;

			while (!stop.IsCancellationRequested)
			{
				if (!cap.Read(frame) || frame.Empty()) { Thread.Sleep(10); continue; }

				CvAruco.DetectMarkers(frame, dict, out var corners, out var ids, prm, out _);
				if (CalibrateRequested) { CalibrateRequested = false; note = Calibrate(corners, ids); }
				for (int i = 0; i < ids.Length; i++)
				{
					var pts = corners[i].Select(p => new CvPoint((int)p.X, (int)p.Y)).ToArray();
					Cv2.Polylines(frame, new[] { pts }, true, Amber, 2);
					Cv2.PutText(frame, "ID" + ids[i], pts[0], HersheyFonts.HersheySimplex, 0.5, Amber, 1);
				}

				var objs = new List<object>();
				int idx = 0;
				foreach (var d in Detect(net, frame, ConfThr, names))
				{
					var c = new Point2d(d.Box.X + d.Box.Width / 2.0, d.Box.Y + d.Box.Height / 2.0);
					var mm = ToMm(c);
					var tl = ToMm(new Point2d(d.Box.Left, d.Box.Top));
					var br = ToMm(new Point2d(d.Box.Right, d.Box.Bottom));
					double[] size = tl != null && br != null
						? new[] { Math.Round(Math.Abs(br.Value.X - tl.Value.X), 1), Math.Round(Math.Abs(br.Value.Y - tl.Value.Y), 1) } : null;
					objs.Add(new
					{
						id = idx++,
						label = d.Label,
						confidence = Math.Round(d.Conf, 2),
						bbox_px = new[] { d.Box.X, d.Box.Y, d.Box.Width, d.Box.Height },
						center_px = new[] { Math.Round(c.X, 1), Math.Round(c.Y, 1) },
						x_mm = mm == null ? (double?)null : Math.Round(mm.Value.X, 1),
						y_mm = mm == null ? (double?)null : Math.Round(mm.Value.Y, 1),
						size_mm = size,   // approximate: box extent along the table axes
					});
					Cv2.Rectangle(frame, d.Box, Green, 2);
					var text = mm == null ? $"{d.Label} {d.Conf:P0}" : $"{d.Label} ({mm.Value.X:F0},{mm.Value.Y:F0})";
					Cv2.PutText(frame, text, new CvPoint(d.Box.X, Math.Max(14, d.Box.Y - 6)), HersheyFonts.HersheySimplex, 0.55, Green, 1);
				}

				string calib;
				lock (Gate) calib = H == null ? "NOT calibrated" : double.IsNaN(calibRmsMm) ? "calibrated (loaded)" : $"calibrated, RMS error {calibRmsMm:F1} mm";
				latestJson = JsonSerializer.Serialize(new
				{
					timestamp = DateTime.UtcNow.ToString("o"),
					calibrated = calib.StartsWith("calibrated"),
					frame = new { width = frame.Width, height = frame.Height },
					objects = objs,
				}, JsonOpts);

				if (++count >= 10) { fps = count / sw.Elapsed.TotalSeconds; count = 0; sw.Restart(); }
				if (!UiPending)
				{
					UiPending = true;
					var bmp = BitmapSourceConverter.ToBitmapSource(frame); bmp.Freeze();
					var js = latestJson;
					var st = $"{fps:F1} fps | {calib}\nmarkers seen: {(ids.Length == 0 ? "none" : string.Join(",", ids.OrderBy(x => x)))}\n{note}";
					ui.InvokeAsync(() =>
					{
						if (stop.IsCancellationRequested) return;
						img.Source = bmp; json.Text = js; status.Text = st; UiPending = false;
					});
				}
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine("Camera/vision failed: " + ex);
			ui.InvokeAsync(() => { if (!stop.IsCancellationRequested) status.Text = "Camera/vision failed: " + ex.Message; });
		}
	})
	{ IsBackground = true, Name = "ArmVision capture" };

	var win = new WinWindow { Title = "Arm vision", Width = 1300, Height = 760, Content = root };
	var cleanupGate = new object();
	bool stopped = false;
	void Cleanup()
	{
		lock (cleanupGate)
		{
			if (stopped) return;
			stop.Cancel();
			try
			{
				http.Close();
			}
			finally
			{
				// Never dispose native vision resources concurrently with Read/Detect.
				// The capture thread's using scopes release them before Join returns.
				if ((worker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
				{
					if (!worker.Join(2000))
					{
						Console.Error.WriteLine("Waiting for camera capture/inference to finish before releasing the device...");
						worker.Join();
					}
				}
				if (httpWorker != null && (httpWorker.ThreadState & System.Threading.ThreadState.Unstarted) == 0)
					httpWorker.Join();
				lock (Gate) { H?.Dispose(); H = null; calibRmsMm = double.NaN; }
				stopped = true;
			}
		}
	}

	EventHandler onCleanup = (s, e) => Cleanup();
	win.Closed += (s, e) => Cleanup();
	Util.Cleanup += onCleanup;
	try
	{
		lock (cleanupGate)
		{
			if (stopped) return;
			httpWorker?.Start();
			worker.Start();
		}
		win.ShowDialog();
	}
	finally
	{
		try { Cleanup(); }
		finally { Util.Cleanup -= onCleanup; }
	}
}

// ---------- calibration ----------
static string Calibrate(Point2f[][] corners, int[] ids)
{
	var src = new List<Point2d>(); var dst = new List<Point2d>();
	for (int i = 0; i < ids.Length; i++)
		if (MarkerMm.TryGetValue(ids[i], out var w)) { src.Add(Center(corners[i])); dst.Add(new Point2d(w.x, w.y)); }
	if (src.Count < 4) return $"Need at least 4 known markers, saw {src.Count}.";
	var h = Cv2.FindHomography(src, dst, HomographyMethods.None);
	var back = Cv2.PerspectiveTransform(src, h);
	double rms = Math.Sqrt(back.Zip(dst, (a, b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)).Average());
	lock (Gate) { H?.Dispose(); H = h; calibRmsMm = rms; }
	SaveCalibration();
	return $"Calibrated from {src.Count} markers. RMS error {rms:F1} mm" + (src.Count == 4 ? " (meaningless with exactly 4; use 6+)." : ".");
}

static Point2d Center(Point2f[] c) => new(c.Average(p => p.X), c.Average(p => p.Y));

static Point2d? ToMm(Point2d px)
{
	lock (Gate) { if (H == null) return null; return Cv2.PerspectiveTransform(new[] { px }, H)[0]; }
}

static string CalibPath => Path.Combine(Path.GetDirectoryName(Util.CurrentQueryPath) ?? Path.GetTempPath(), "table_calib.json");

static void SaveCalibration()
{
	lock (Gate)
	{
		var h = Enumerable.Range(0, 9).Select(i => H.At<double>(i / 3, i % 3)).ToArray();
		File.WriteAllText(CalibPath, JsonSerializer.Serialize(new { h, rms = double.IsNaN(calibRmsMm) ? 0 : calibRmsMm }));
	}
}

static void LoadCalibration()
{
	try
	{
		if (!File.Exists(CalibPath))
			return;
		using var doc = JsonDocument.Parse(File.ReadAllText(CalibPath));
		var a = doc.RootElement.GetProperty("h").EnumerateArray().Select(x => x.GetDouble()).ToArray();
		var m = new Mat(3, 3, MatType.CV_64FC1);
		for (int i = 0; i < 9; i++)
			m.Set(i / 3, i % 3, a[i]);
		lock (Gate) H = m;
	}
	catch { /* ignore a corrupt file; recalibrate */ }
}

// ---------- YOLO (ultralytics ONNX: [1, 4+classes, anchors], or NMS-free [1, N, 6]) ----------
record Det(string Label, float Conf, CvRect Box);

static List<Det> Detect(InferenceSession net, Mat frame, float confThr, string[] names)
{
	int w = frame.Width, h = frame.Height;
	float scale = Math.Min((float)InputSize / w, (float)InputSize / h);
	int nw = (int)Math.Round(w * scale), nh = (int)Math.Round(h * scale);
	int padX = (InputSize - nw) / 2, padY = (InputSize - nh) / 2;

	using var resized = new Mat();
	Cv2.Resize(frame, resized, new CvSize(nw, nh));
	using var canvas = new Mat(InputSize, InputSize, MatType.CV_8UC3, new Scalar(114, 114, 114));
	using (var roi = new Mat(canvas, new CvRect(padX, padY, nw, nh))) resized.CopyTo(roi);
	using var blob = CvDnn.BlobFromImage(canvas, scaleFactor: 1 / 255.0, size: new CvSize(InputSize, InputSize), swapRB: true, crop: false);
	var pixels = new float[3 * InputSize * InputSize];
	System.Runtime.InteropServices.Marshal.Copy(blob.Data, pixels, 0, pixels.Length);
	using var input = OrtValue.CreateTensorValueFromMemory(pixels, new long[] { 1, 3, InputSize, InputSize });
	using var options = new RunOptions();
	using var outputs = net.Run(options, net.InputNames, new[] { input }, net.OutputNames);
	var o = outputs[0];
	var shape = o.GetTensorTypeAndShape().Shape;
	if (shape.Length != 3 || shape[0] != 1)
		throw new NotSupportedException($"Unexpected YOLO output shape: [{string.Join(", ", shape)}]");
	var data = o.GetTensorDataAsSpan<float>().ToArray();

	string Name(int c) => c >= 0 && c < names.Length ? names[c] : "class" + c;
	CvRect Unmap(float x1, float y1, float x2, float y2)
	{
		int a = Math.Clamp((int)((x1 - padX) / scale), 0, w - 1), b = Math.Clamp((int)((y1 - padY) / scale), 0, h - 1);
		int c = Math.Clamp((int)((x2 - padX) / scale), 0, w - 1), d = Math.Clamp((int)((y2 - padY) / scale), 0, h - 1);
		return new CvRect(a, b, Math.Max(1, c - a), Math.Max(1, d - b));
	}

	var result = new List<Det>();
	if (shape[2] == 6)
	{
		int n = checked((int)shape[1]);
		var d = data;
		for (int i = 0; i < n; i++)
			if (d[i * 6 + 4] >= confThr)
				result.Add(new Det(Name((int)d[i * 6 + 5]), d[i * 6 + 4], Unmap(d[i * 6], d[i * 6 + 1], d[i * 6 + 2], d[i * 6 + 3])));
		return result;
	}

	int attrs = checked((int)shape[1]), count = checked((int)shape[2]);
	var boxes = new List<CvRect>(); var scores = new List<float>(); var classes = new List<int>();
	for (int i = 0; i < count; i++)
	{
		float best = 0; int bestC = -1;
		for (int c = 4; c < attrs; c++) { float s = data[c * count + i]; if (s > best) { best = s; bestC = c - 4; } }
		if (best < confThr) continue;
		float cx = data[i], cy = data[count + i], bw = data[2 * count + i], bh = data[3 * count + i];
		boxes.Add(Unmap(cx - bw / 2, cy - bh / 2, cx + bw / 2, cy + bh / 2)); scores.Add(best); classes.Add(bestC);
	}
	CvDnn.NMSBoxes(boxes, scores, confThr, 0.45f, out int[] keep);
	foreach (var k in keep) result.Add(new Det(Name(classes[k]), scores[k], boxes[k]));
	return result;
}

static readonly string[] Coco = ("cube,person,bicycle,car,motorcycle,airplane,bus,train,truck,boat,traffic light,fire hydrant,stop sign,parking meter,bench,bird,cat,dog,horse,sheep,cow,elephant,bear,zebra,giraffe,backpack,umbrella,handbag,tie,suitcase,frisbee,skis,snowboard,sports ball,kite,baseball bat,baseball glove,skateboard,surfboard,tennis racket,bottle,wine glass,cup,fork,knife,spoon,bowl,banana,apple,sandwich,orange,broccoli,carrot,hot dog,pizza,donut,cake,chair,couch,potted plant,bed,dining table,toilet,tv,laptop,mouse,remote,keyboard,cell phone,microwave,oven,toaster,sink,refrigerator,book,clock,vase,scissors,teddy bear,hair drier,toothbrush").Split(',');
