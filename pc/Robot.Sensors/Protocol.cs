using System.Globalization;
using System.Text;

namespace Robot.Sensors;

[Flags]
public enum SensorStatus : ushort
{
    Good = 0, RangeFault = 1, ColorFault = 2, Simulated = 4,
    TransmitDropped = 8, CommandsRejected = 16
}

[Flags]
public enum SensorCapabilities : byte
{
    Range = 1, Infrared = 2, Touch = 4, Light = 8, Sound = 16, Rgb = 32, Clear = 64
}

public abstract record DeviceMessage;
public sealed record HelloFrame(byte Version, string Firmware, byte ResetCause,
    SensorCapabilities Capabilities, uint CommandsRejected, uint TransmitDropped,
    uint MaxLoopMicroseconds) : DeviceMessage;
public sealed record SensorFrame(uint Sequence, uint Milliseconds, ushort RangeMm,
    byte Infrared, bool Touch, ushort Light, ushort Sound, ushort Red, ushort Green,
    ushort Blue, ushort Clear, SensorStatus Status) : DeviceMessage;
public sealed record SensorEvent(uint Sequence, uint Milliseconds, string Source, bool Value) : DeviceMessage;

public static class Protocol
{
    public const int MaxLineLength = 128;
    public const byte Version = 2;
    public const int BaudRate = 115200;

    public static string Encode(string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(payload);
        if (payload.Length + 3 > MaxLineLength ||
            payload.Any(c => c < 32 || c > 126 || c == '*'))
            throw new ArgumentException("Payload must be printable ASCII without '*' and fit in 128 bytes.", nameof(payload));
        byte checksum = 0;
        foreach (char c in payload) checksum ^= (byte)c;
        return $"{payload}*{checksum:X2}\n";
    }

    public static bool TryParse(string line, out DeviceMessage? message, out string error)
    {
        message = null;
        error = "Malformed frame";
        if (line.Length > MaxLineLength || line.Length < 4 ||
            line.Any(c => c < 32 || c > 126)) return false;
        int star = line.Length - 3;
        if (line[star] != '*' || line.IndexOf('*') != star ||
            !byte.TryParse(line.AsSpan(star + 1), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out byte expected)) return false;
        byte checksum = 0;
        foreach (char c in line.AsSpan(0, star)) checksum ^= (byte)c;
        if (checksum != expected) { error = "Checksum mismatch"; return false; }
        string[] fields = line[..star].Split(',');
        bool Number(int index, uint max, out uint value) =>
            uint.TryParse(fields[index], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value <= max;
        switch (fields[0])
        {
            case "H" when fields.Length == 8:
                if (!Number(1, byte.MaxValue, out uint version) || version != Version ||
                    fields[2].Length is < 1 or > 16 ||
                    !Number(3, byte.MaxValue, out uint reset) ||
                    !Number(4, 127, out uint mask) || !Number(5, uint.MaxValue, out uint rejected) ||
                    !Number(6, uint.MaxValue, out uint dropped) || !Number(7, uint.MaxValue, out uint loop)) break;
                message = new HelloFrame((byte)version, fields[2], (byte)reset, (SensorCapabilities)mask, rejected, dropped, loop);
                break;
            case "S" when fields.Length == 13:
                if (!Number(1, uint.MaxValue, out uint s) || !Number(2, uint.MaxValue, out uint t) ||
                    !Number(3, ushort.MaxValue, out uint distance) || fields[4].Length != 3 ||
                    fields[4].Any(c => c is not ('0' or '1')) ||
                    !Number(5, 1, out uint touched) || !Number(6, 1023, out uint adcLight) ||
                    !Number(7, 1023, out uint adcSound) || !Number(8, ushort.MaxValue, out uint r) ||
                    !Number(9, ushort.MaxValue, out uint g) || !Number(10, ushort.MaxValue, out uint b) ||
                    !Number(11, ushort.MaxValue, out uint c) || !Number(12, 31, out uint flags)) break;
                byte ir = (byte)((fields[4][0] - '0') | ((fields[4][1] - '0') << 1) | ((fields[4][2] - '0') << 2));
                message = new SensorFrame(s, t, (ushort)distance, ir, touched != 0,
                    (ushort)adcLight, (ushort)adcSound, (ushort)r, (ushort)g, (ushort)b,
                    (ushort)c, (SensorStatus)flags);
                break;
            case "E" when fields.Length == 5:
                if (!Number(1, uint.MaxValue, out uint eventSeq) || !Number(2, uint.MaxValue, out uint eventMs) ||
                    fields[3] is not ("IR0" or "IR1" or "IR2" or "TOUCH") || !Number(4, 1, out uint value)) break;
                message = new SensorEvent(eventSeq, eventMs, fields[3], value != 0);
                break;
        }
        if (message is null) { error = "Unsupported version, message kind, field count or value"; return false; }
        error = "";
        return true;
    }
}

public sealed record FramingResult(string? Line, string? Error);
public sealed record FramingState(string Partial = "", bool Discarding = false);
public sealed record FramingBatch(FramingState State, IReadOnlyList<FramingResult> Results);

// Explicit immutable state keeps framing a pure, bounded function.
public static class LineFramer
{
    public static FramingBatch Push(FramingState state, ReadOnlySpan<byte> bytes)
    {
        var partial = new StringBuilder(state.Partial);
        bool discarding = state.Discarding;
        var result = new List<FramingResult>();
        foreach (byte value in bytes)
        {
            if (value == '\n')
            {
                if (partial.Length > 0 && partial[^1] == '\r') partial.Length--;
                if (discarding || partial.Length > Protocol.MaxLineLength)
                    result.Add(new(null, "Oversized or non-ASCII line"));
                else result.Add(new(partial.ToString(), null));
                partial.Clear();
                discarding = false;
            }
            else if (!discarding)
            {
                if (partial.Length == Protocol.MaxLineLength + 1 || (value != '\r' && (value < 32 || value > 126)))
                    discarding = true;
                else partial.Append((char)value);
            }
        }
        return new(new(partial.ToString(), discarding), result);
    }
}

public sealed class SequenceTracker
{
    private uint? previous;
    public void Reset() => previous = null;
    public string? Observe(uint sequence)
    {
        string? error = previous.HasValue && sequence != unchecked(previous.Value + 1)
            ? $"Sequence discontinuity: expected {unchecked(previous.Value + 1)}, received {sequence}" : null;
        previous = sequence;
        return error;
    }
}
