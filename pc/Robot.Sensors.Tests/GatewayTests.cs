using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text;
using Xunit;

namespace Robot.Sensors.Tests;

public sealed class GatewayTests
{
    private sealed class ScriptedConnection(params string[] chunks) : IDeviceConnection
    {
        private readonly Queue<string> remaining = new(chunks);
        public bool Disposed { get; private set; }
        public int Read(byte[] buffer)
        {
            if (remaining.TryDequeue(out var text)) return Encoding.ASCII.GetBytes(text, buffer);
            Thread.Sleep(10);
            throw new TimeoutException();
        }
        public void Write(string wireMessage) { }
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public async Task GatewaySharesTransportCountsRejectsAndDetectsGapsBeforeDeduplication()
    {
        var connection = new ScriptedConnection(FakeArduino.Hello,
            "garbage\n" + FakeArduino.Telemetry(0, 0),
            Protocol.Encode("E,1,1,IR0,1") + FakeArduino.Telemetry(3, 50));
        int opens = 0;
        var gateway = new SensorGateway(() => { Interlocked.Increment(ref opens); return connection; });
        var updates = new ConcurrentQueue<GatewayUpdate>();
        using var all = gateway.Updates.Subscribe(updates.Enqueue);
        using var frames = gateway.Frames.Subscribe(_ => { });
        await WaitUntil(() => updates.Count(x => x.Message is SensorFrame) == 2);
        Assert.Equal(1, opens);
        Assert.Contains(updates, x => x.Health?.Kind == HealthKind.Rejected && x.Health.RejectCount == 1);
        Assert.Contains(updates, x => x.Health?.Kind == HealthKind.SequenceGap);
        Assert.Contains(updates, x => x.Message is SensorEvent);
    }

    [Fact]
    public async Task SilenceIsHealthSignalAndStreamRecoversWithoutTerminating()
    {
        int reads = 0;
        var gateway = new SensorGateway(() => new RecoveringConnection(() => Interlocked.Increment(ref reads)),
            TimeSpan.FromMilliseconds(40));
        var health = new ConcurrentQueue<SensorHealth>();
        using var subscription = gateway.Health.Subscribe(health.Enqueue);
        await WaitUntil(() => health.Count(h => h.Kind == HealthKind.Recovered) >= 2);
        Assert.Contains(health, h => h.Kind == HealthKind.Silence);
    }

    private sealed class RecoveringConnection(Func<int> nextRead) : IDeviceConnection
    {
        public int Read(byte[] buffer)
        {
            Thread.Sleep(15);
            int read = nextRead();
            if (read == 1) return Encoding.ASCII.GetBytes(FakeArduino.Telemetry(0, 0), buffer);
            if (read == 8) return Encoding.ASCII.GetBytes(FakeArduino.Telemetry(1, 150), buffer);
            throw new TimeoutException();
        }
        public void Write(string wireMessage) { }
        public void Dispose() { }
    }

    [Fact]
    public async Task FailedOpenEmitsHealthAndReconnects()
    {
        int attempts = 0;
        var gateway = new SensorGateway(() => Interlocked.Increment(ref attempts) == 1
            ? throw new IOException("unplugged")
            : new ScriptedConnection(FakeArduino.Telemetry(0, 0)));
        var updates = new ConcurrentQueue<GatewayUpdate>();
        using var subscription = gateway.Updates.Subscribe(updates.Enqueue);
        await WaitUntil(() => updates.Any(x => x.Message is SensorFrame));
        Assert.Equal(2, attempts);
        Assert.Contains(updates, x => x.Health?.Kind == HealthKind.Disconnected && x.Health.Detail.Contains("1s"));
    }

    [Fact]
    public async Task UnsubscribingLastObserverDisposesConnection()
    {
        var connection = new ScriptedConnection(FakeArduino.Telemetry(0, 0));
        var gateway = new SensorGateway(() => connection);
        using var subscription = gateway.Frames.Subscribe(_ => { });
        await Task.Delay(50);
        subscription.Dispose();
        await WaitUntil(() => connection.Disposed);
    }

    [Fact]
    public async Task DiagnosticHelloDoesNotEraseSequenceGaps()
    {
        var gateway = new SensorGateway(() => new ScriptedConnection(
            FakeArduino.Hello + FakeArduino.Telemetry(0, 0),
            Protocol.Encode("H,2,fake-1.0,0,63,0,1,100") + FakeArduino.Telemetry(2, 50)));
        var updates = new ConcurrentQueue<GatewayUpdate>();
        using var subscription = gateway.Updates.Subscribe(updates.Enqueue);
        await WaitUntil(() => updates.Count(x => x.Message is SensorFrame) == 2);
        Assert.Contains(updates, x => x.Health?.Kind == HealthKind.SequenceGap);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
}
