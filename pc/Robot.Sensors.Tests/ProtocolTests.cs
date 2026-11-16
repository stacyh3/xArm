using System.Text;
using Xunit;

namespace Robot.Sensors.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void FakeFramesRoundTripIncludingCapabilitiesAndSimulationFlag()
    {
        Assert.True(Protocol.TryParse(FakeArduino.Hello.TrimEnd('\n'), out var hello, out _));
        Assert.False(Assert.IsType<HelloFrame>(hello).Capabilities.HasFlag(SensorCapabilities.Clear));
        Assert.True(Protocol.TryParse(FakeArduino.Telemetry(uint.MaxValue, uint.MaxValue).TrimEnd('\n'), out var parsed, out _));
        var frame = Assert.IsType<SensorFrame>(parsed);
        Assert.Equal(uint.MaxValue, frame.Sequence);
        Assert.Equal((ushort)250, frame.RangeMm);
        Assert.Equal(SensorStatus.Simulated, frame.Status);
    }

    [Fact]
    public void ChecksumMatchesKnownPingVector()
    {
        Assert.Equal("P*50\n", Protocol.Encode("P"));
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("P*50")]
    [InlineData("S,1,1,2,000,0,1,1,1,1,1,1,0*00")]
    [InlineData("H,1,1.0,0,63,0,0,0*00")]
    public void InvalidFramesAreRejected(string line) =>
        Assert.False(Protocol.TryParse(line, out _, out _));

    [Theory]
    [InlineData("S,1,1,2,020,0,1,1,1,1,1,1,0")]
    [InlineData("S,1,1,2,000,2,1,1,1,1,1,1,0")]
    [InlineData("S,1,1,2,000,0,1024,1,1,1,1,1,0")]
    [InlineData("S,-1,1,2,000,0,1,1,1,1,1,1,0")]
    [InlineData("S,1,1,2,000,0,1,1,1,1,1,1,0,EXTRA")]
    [InlineData("H,1,1.0,0,63,0,0,0")]
    [InlineData("E,0,0,ARM,1")]
    public void ValidChecksumDoesNotPermitInvalidFields(string payload) =>
        Assert.False(Protocol.TryParse(Protocol.Encode(payload).TrimEnd('\n'), out _, out _));

    [Fact]
    public void PartialAndMultipleLinesAreFramedWithoutLoss()
    {
        var state = new FramingState();
        string wire = FakeArduino.Hello + FakeArduino.Telemetry(0, 0).Replace("\n", "\r\n");
        var results = new List<FramingResult>();
        foreach (byte value in Encoding.ASCII.GetBytes(wire))
        {
            FramingBatch batch = LineFramer.Push(state, [value]);
            state = batch.State;
            results.AddRange(batch.Results);
        }
        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(Protocol.TryParse(result.Line!, out _, out _)));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    [InlineData(4096, true)]
    public void OversizeIsRejectedOnceAndNextLineResynchronizes(int size, bool rejected)
    {
        var result = LineFramer.Push(new(), Encoding.ASCII.GetBytes(new string('x', size) + "\n" + FakeArduino.Hello)).Results;
        Assert.Equal(2, result.Count);
        Assert.Equal(rejected, result[0].Error is not null);
        Assert.True(Protocol.TryParse(result[1].Line!, out _, out _));
    }

    [Fact]
    public void NonAsciiGarbageIsNotReplacedByPlausibleAscii()
    {
        var result = LineFramer.Push(new(), [0xFF, (byte)'\n']).Results;
        Assert.NotNull(Assert.Single(result).Error);
    }

    [Fact]
    public void SequenceTrackerIncludesEventsWrapsAndCanReset()
    {
        var tracker = new SequenceTracker();
        Assert.Null(tracker.Observe(uint.MaxValue));
        Assert.Null(tracker.Observe(0));
        Assert.NotNull(tracker.Observe(2));
        Assert.NotNull(tracker.Observe(2));
        tracker.Reset();
        Assert.Null(tracker.Observe(100));
        Assert.True(Protocol.TryParse(Protocol.Encode("E,101,20,TOUCH,1").TrimEnd('\n'), out var parsed, out _));
        Assert.True(Assert.IsType<SensorEvent>(parsed).Value);
    }
}
