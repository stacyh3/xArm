using Robot.Sensors;
using Xunit;

namespace Robot.Dashboard.Wpf.Tests;

public sealed class DashboardTests
{
    private static SensorFrame Frame(SensorStatus status = SensorStatus.Good) =>
        new(42, 1000, 250, 5, true, 600, 80, 120, 100, 60, 300, status);

    private static DashboardViewModel Connected()
    {
        var model = new DashboardViewModel();
        model.Begin(false, "COM4");
        model.ApplyHealth(new(HealthKind.Recovered, "Telemetry flowing", DateTimeOffset.Now));
        model.ApplyFrame(Frame(), 1);
        return model;
    }

    [Fact]
    public void ReadingsAreNotPresentedAsCurrentBeforeTelemetryRecovery()
    {
        var model = new DashboardViewModel();
        model.Begin(false, "COM4");
        model.ApplyFrame(Frame(), 1);
        model.TickHistory();
        Assert.False(model.IsFresh);
        Assert.Equal("WAITING FOR DATA", model.StateLabel);
        Assert.Null(Assert.Single(model.RangeHistory));
    }

    [Theory]
    [InlineData(HealthKind.Silence)]
    [InlineData(HealthKind.Disconnected)]
    public void ConnectionFailureMarksReadingsStaleAndChartsLeaveGaps(HealthKind kind)
    {
        var model = Connected();
        model.TickHistory();
        model.ApplyMean(75);
        model.ApplyHealth(new(kind, "Link lost", DateTimeOffset.Now));
        // A frame already sampled before the health notification must not remove stale state.
        model.ApplyFrame(Frame(), 2);
        model.TickHistory();
        Assert.False(model.IsFresh);
        Assert.Equal("STALE DATA", model.StateLabel);
        Assert.Equal("--", model.SoundMean);
        Assert.Equal(new double?[] { 250, null }, model.RangeHistory);
        model.ApplyHealth(new(HealthKind.Recovered, "Resumed", DateTimeOffset.Now));
        model.TickHistory();
        Assert.Equal(250, model.RangeHistory[^1]);
    }

    [Fact]
    public void WarningDoesNotReplaceConnectionHealthAndRemainsInEventLog()
    {
        var model = Connected();
        model.ApplyHealth(new(HealthKind.SequenceGap, "Expected 1, got 3", DateTimeOffset.Now));
        Assert.True(model.IsFresh);
        Assert.Equal("LIVE", model.StateLabel);
        Assert.Contains("Expected 1", model.Notice);
        model.ApplyHealth(new(HealthKind.Recovered, "Telemetry flowing", DateTimeOffset.Now));
        Assert.True(model.HasNotice);
        model.ClearNotice();
        Assert.False(model.HasNotice);
        Assert.Contains(model.Log, item => item.Kind == "SEQUENCEGAP");
    }

    [Fact]
    public void FaultsAreExplicitAndNotPlottedAsValidZeroes()
    {
        var model = Connected();
        model.ApplyFrame(Frame(SensorStatus.RangeFault | SensorStatus.ColorFault), 2);
        model.TickHistory();
        Assert.Equal("FAULT", model.Range);
        Assert.Equal("FAULT", model.Red);
        Assert.Equal("FAULT", model.Clear);
        Assert.Null(Assert.Single(model.RangeHistory));
        Assert.Equal(600, Assert.Single(model.LightHistory));
    }

    [Fact]
    public void ColorClearIsOnlyDisplayedWhenAdvertised()
    {
        var model = Connected();
        model.ApplyHello(new(2, "1.0", 255, (SensorCapabilities)63, 0, 0, 3652));
        Assert.Equal("N/A", model.Clear);
        model.ApplyHello(new(2, "1.0", 255, (SensorCapabilities)127, 0, 0, 3652));
        Assert.Equal("300", model.Clear);
        Assert.Contains("APDS9960", model.ColorDescription);
        Assert.Contains("unknown", model.DeviceDetails);
    }

    [Fact]
    public void SimulationAndDigitalInputsHaveExplicitLabels()
    {
        var model = Connected();
        model.ApplyFrame(Frame(SensorStatus.Simulated), 1);
        Assert.Equal("SIMULATION", model.StateLabel);
        Assert.Contains("synthetic", model.ModeDescription);
        Assert.Equal("DETECTED", model.Left);
        Assert.Equal("CLEAR", model.Center);
        Assert.Equal("DETECTED", model.Right);
        Assert.Equal("TOUCHED", model.Touch);
    }

    [Fact]
    public void HistoryAndLogAreBoundedAndNewSessionClearsOldData()
    {
        var model = Connected();
        for (int i = 0; i < 350; i++)
        {
            model.TickHistory();
            model.ApplyEvent(new((uint)i, (uint)i, "TOUCH", true));
        }
        Assert.Equal(300, model.RangeHistory.Length);
        Assert.Equal(200, model.Log.Count);
        Assert.Contains("seq 349", model.Log[0].Detail);
        model.End();
        Assert.False(model.IsFresh);
        Assert.True(model.CanConnect);
        model.Begin(true, DashboardViewModel.SimulationPort);
        Assert.Empty(model.RangeHistory);
        Assert.Equal("--", model.Range);
        Assert.Single(model.Log);
        Assert.False(model.CanConnect);
    }
}
