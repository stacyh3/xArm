using System.Diagnostics;
using System.Reactive.Linq;

namespace Robot.Sensors;

public enum HealthKind { Connected, Disconnected, Rejected, SequenceGap, Silence, Recovered }
public sealed record SensorHealth(HealthKind Kind, string Detail, DateTimeOffset At, long RejectCount = 0);
public sealed record GatewayUpdate(DeviceMessage? Message = null, SensorHealth? Health = null);

public sealed class SensorGateway
{
    public IObservable<GatewayUpdate> Updates { get; }
    public IObservable<SensorFrame> Frames { get; }
    public IObservable<SensorEvent> Events { get; }
    public IObservable<HelloFrame> Hellos { get; }
    public IObservable<SensorHealth> Health { get; }

    public SensorGateway(Func<IDeviceConnection> connect, TimeSpan? silenceTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(connect);
        TimeSpan silence = silenceTimeout ?? TimeSpan.FromSeconds(2);
        if (silence <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
        Updates = Observable.Create<GatewayUpdate>((observer, cancellationToken) =>
            Task.Factory.StartNew(() =>
            {
                long rejects = 0;
                int backoff = 1;
                void HealthEvent(HealthKind kind, string detail) =>
                    observer.OnNext(new(Health: new(kind, detail, DateTimeOffset.UtcNow, rejects)));
                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        using IDeviceConnection connection = connect();
                        // Serial reads have a finite timeout; disposal also interrupts a blocked read.
                        using var registration = cancellationToken.Register(connection.Dispose);
                        var framing = new FramingState();
                        var sequences = new SequenceTracker();
                        var buffer = new byte[512];
                        long lastFrame = Stopwatch.GetTimestamp();
                        long lastPing = lastFrame;
                        bool silent = false;
                        bool receivedTelemetry = false;
                        HelloFrame? previousHello = null;
                        HealthEvent(HealthKind.Connected, "Device link opened; awaiting telemetry");
                        connection.Write(Protocol.Encode("P"));
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            int count;
                            try { count = connection.Read(buffer); }
                            catch (TimeoutException) { count = 0; }
                            if (count < 0 || count > buffer.Length)
                                throw new IOException("Transport returned an invalid byte count.");
                            FramingBatch batch = LineFramer.Push(framing, buffer.AsSpan(0, count));
                            framing = batch.State;
                            foreach (FramingResult item in batch.Results)
                            {
                                DeviceMessage? message = null;
                                string error = item.Error ?? "";
                                if (item.Line is null || !Protocol.TryParse(item.Line, out message, out error))
                                {
                                    rejects++;
                                    HealthEvent(HealthKind.Rejected, error);
                                    continue;
                                }
                                if (message is HelloFrame hello)
                                {
                                    if (previousHello is not null && hello != previousHello)
                                    {
                                        // Diagnostics change on ping, so only reset on firmware/reset identity.
                                        if (hello.Firmware != previousHello.Firmware ||
                                            hello.ResetCause != previousHello.ResetCause)
                                            sequences.Reset();
                                    }
                                    previousHello = hello;
                                }
                                uint? sequence = message switch
                                {
                                    SensorFrame frame => frame.Sequence,
                                    SensorEvent evt => evt.Sequence,
                                    _ => null
                                };
                                if (sequence is uint seq && sequences.Observe(seq) is string gap)
                                    HealthEvent(HealthKind.SequenceGap, gap);
                                if (message is SensorFrame)
                                {
                                    lastFrame = Stopwatch.GetTimestamp();
                                    backoff = 1;
                                    if (silent || !receivedTelemetry)
                                        HealthEvent(HealthKind.Recovered, "Telemetry flowing");
                                    silent = false;
                                    receivedTelemetry = true;
                                }
                                observer.OnNext(new(Message: message));
                            }
                            if (!silent && Stopwatch.GetElapsedTime(lastFrame) >= silence)
                            {
                                silent = true;
                                HealthEvent(HealthKind.Silence, $"No telemetry for {silence.TotalSeconds:g} seconds");
                            }
                            if (Stopwatch.GetElapsedTime(lastPing) >= TimeSpan.FromSeconds(5))
                            {
                                connection.Write(Protocol.Encode("P"));
                                lastPing = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                    catch (Exception ex) when (cancellationToken.IsCancellationRequested &&
                        ex is IOException or ObjectDisposedException or InvalidOperationException or OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                        System.Security.SecurityException or TimeoutException)
                    {
                        HealthEvent(HealthKind.Disconnected, $"{ex.Message} Retrying in {backoff}s.");
                    }
                    if (cancellationToken.IsCancellationRequested) break;
                    if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(backoff))) break;
                    backoff = Math.Min(16, backoff * 2);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).Publish().RefCount();
        Frames = Updates.Where(update => update.Message is SensorFrame).Select(update => (SensorFrame)update.Message!);
        Events = Updates.Where(update => update.Message is SensorEvent).Select(update => (SensorEvent)update.Message!);
        Hellos = Updates.Where(update => update.Message is HelloFrame).Select(update => (HelloFrame)update.Message!);
        Health = Updates.Where(update => update.Health is not null).Select(update => update.Health!);
    }
}
