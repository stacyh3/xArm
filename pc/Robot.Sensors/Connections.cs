using System.IO.Ports;
using System.Text;

namespace Robot.Sensors;

public interface IDeviceConnection : IDisposable
{
    int Read(byte[] buffer);
    void Write(string wireMessage);
}

public sealed class SerialConnection : IDeviceConnection
{
    private readonly SerialPort port;
    public SerialConnection(string portName)
    {
        port = new SerialPort(portName, Protocol.BaudRate)
        {
            ReadTimeout = 200, WriteTimeout = 500, DtrEnable = true,
            Encoding = Encoding.ASCII, NewLine = "\n"
        };
        try { port.Open(); }
        catch { port.Dispose(); throw; }
    }
    public int Read(byte[] buffer) => port.Read(buffer, 0, buffer.Length);
    public void Write(string wireMessage) => port.Write(wireMessage);
    public void Dispose() => port.Dispose();
}

public static class FakeArduino
{
    public static string Hello => Protocol.Encode("H,2,fake-1.0,0,63,0,0,0");

    public static string Telemetry(uint sequence, uint milliseconds)
    {
        ushort sound = (ushort)((milliseconds / 20) % 100);
        return Protocol.Encode(FormattableString.Invariant(
            $"S,{sequence},{milliseconds},250,000,0,512,{sound},120,80,60,0,4"));
    }

    public static async Task RunSerialAsync(string portName, CancellationToken cancellationToken)
    {
        using var port = new SerialPort(portName, Protocol.BaudRate)
        {
            WriteTimeout = 500, ReadTimeout = 200, Encoding = Encoding.ASCII
        };
        port.Open();
        port.Write(Hello);
        uint sequence = 0;
        var commands = new FramingState();
        var inputBuffer = new byte[128];
        var clock = System.Diagnostics.Stopwatch.StartNew();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            port.Write(Telemetry(sequence++, unchecked((uint)clock.ElapsedMilliseconds)));
            // Ping is supported for gateway diagnostics; other commands are not simulated.
            if (port.BytesToRead > 0)
            {
                int count = port.Read(inputBuffer, 0, Math.Min(inputBuffer.Length, port.BytesToRead));
                FramingBatch batch = LineFramer.Push(commands, inputBuffer.AsSpan(0, count));
                commands = batch.State;
                foreach (var command in batch.Results)
                {
                    if (command.Line + "\n" == Protocol.Encode("P")) port.Write(Hello);
                    else Console.Error.WriteLine("Fake Arduino rejected an unsupported or malformed command.");
                }
            }
        }
    }
}

public sealed class FakeConnection : IDeviceConnection
{
    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly ManualResetEventSlim disposed = new();
    private uint sequence;
    private bool hello = true;
    private long nextTick;
    public int Read(byte[] buffer)
    {
        if (disposed.IsSet) throw new ObjectDisposedException(nameof(FakeConnection));
        if (hello)
        {
            hello = false;
            return Encoding.ASCII.GetBytes(FakeArduino.Hello, buffer);
        }
        // Absolute scheduling avoids accumulating Windows wait-resolution drift.
        nextTick += 50;
        long remaining = nextTick - clock.ElapsedMilliseconds;
        if (remaining > 0 && disposed.Wait(TimeSpan.FromMilliseconds(remaining)))
            throw new ObjectDisposedException(nameof(FakeConnection));
        if (clock.ElapsedMilliseconds - nextTick > 250) nextTick = clock.ElapsedMilliseconds;
        string line = FakeArduino.Telemetry(sequence++, unchecked((uint)clock.ElapsedMilliseconds));
        return Encoding.ASCII.GetBytes(line, buffer);
    }
    public void Write(string wireMessage)
    {
        if (wireMessage == Protocol.Encode("P")) hello = true;
        else throw new NotSupportedException("In-process fake supports ping only.");
    }
    public void Dispose() => disposed.Set();
}
