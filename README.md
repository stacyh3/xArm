# xArm UNO sensor publisher

Arduino UNO R3 sensor telemetry and a .NET 10 WPF dashboard for Windows, plus
terminal tools. The Arduino
only reports sensors: this code never drives the arm.

Design and protocol: [docs/approach.md](docs/approach.md).
Verified lesson wiring: [docs/sensor-map.md](docs/sensor-map.md).
Implementation status and next-session handoff: [docs/status.md](docs/status.md).

## LINQPad camera prototype

[linqpad/ArmVision.linq](linqpad/ArmVision.linq) is a standalone webcam/YOLO
prototype, separate from the sensor dashboard. Configure its model path and
camera index before running it. Closing its window or stopping the LINQPad
query runs the same cleanup: stop acquisition, close the HTTP listener, and
wait for the capture thread to release the camera and native vision resources.
Shutdown can take longer than two seconds while camera startup or inference
finishes; a waiting message is written rather than abandoning the device.
Capture/inference errors are reported in the window and query error output.

If an older query already left the camera locked, stop that query or close
its LINQPad query process before testing the updated version.

## Graphical dashboard (Windows)

Requires Windows and the .NET 10 SDK. From the repository root in PowerShell:

```powershell
# Open the window and choose a COM port or simulation, then click Connect:
dotnet run --project .\pc\Robot.Dashboard.Wpf
# Start immediately with synthetic data:
dotnet run --project .\pc\Robot.Dashboard.Wpf -- --fake
# Start immediately with the connected UNO:
dotnet run --project .\pc\Robot.Dashboard.Wpf -- COM4
```

The WPF dashboard provides:

- Separated sensor cards with large readings, units and analog level bars.
- Thirty-second rolling distance/light/sound charts, updated at 10 Hz.
- Labeled, highlighted IR and touch states and raw RGB/clear readings.
- An Rx one-second sound mean; sensor acquisition stays at the device rate.
- Explicit simulation, waiting, stale and reconnecting states. Stale readings
  are dimmed and chart gaps are drawn for stale/faulted samples.
- A newest-first, bounded 200-entry event/health log. Protocol warnings are
  separate from connection health and stay visible until dismissed.
- Connect/disconnect and port refresh controls, diagnostics and capability-aware
  clear-channel display. Close/disconnect releases the serial connection.

No whole-window clearing is used. It reuses the same SensorGateway, parser
and fake Arduino as the terminal tools. Each connection has bounded chart/log
history; a new session clears the previous session's data. The distance chart
autoscales; ADC charts keep a fixed 0..1023 scale. Color counts are not converted
to a misleading calibrated color swatch.

Close other serial clients before connecting. Opening a real UNO port normally
resets it; startup sequence discontinuities may appear in the health log.

### WPF tests

```powershell
dotnet test .\pc\Robot.Dashboard.Wpf.Tests\Robot.Dashboard.Wpf.Tests.csproj
```

Tests cover stale/fault presentation, capability handling, simulation labeling,
bounded history/logs and session resets. An STA UI smoke test opens the real
window with fake data, checks bindings/readings/chart updates, and exercises
disconnect/reconnect. For an optional screenshot from that test, set
`XARM_WPF_SCREENSHOT` to a writable PNG file path before running the tests.

## Terminal tools

The original terminal dashboard and virtual-serial fake are retained:

```powershell
dotnet test .\pc\Robot.Sensors.Tests\Robot.Sensors.Tests.csproj
dotnet run --project .\pc\Robot.Dashboard -- --fake
dotnet run --project .\pc\Robot.Dashboard -- --ports
dotnet run --project .\pc\Robot.Dashboard -- COM5
```

Replace COM5 with the UNO's port. Close Arduino Serial Monitor first. Opening
the port enables DTR and normally resets an UNO. Ctrl+C stops the dashboard.
It shows raw values, faults, sequence/parse health, digital events, distinct
value counts, and a one-second Rx sound average. Invalid data does not silently
become a valid measurement. Disconnection/silence marks displayed data stale.

`--fake` uses an in-process fake with the same wire encoder, framer, parser and
gateway as hardware. It requires no serial driver and is clearly labeled.
For a real virtual serial integration test, create a paired connection
using an independently installed virtual COM driver (for example com0com).
This repository does not install drivers or create system ports:

```powershell
# In separate terminals, using your paired ports:
dotnet run --project .\pc\Robot.Dashboard -- --fake-arduino COM20
dotnet run --project .\pc\Robot.Dashboard -- COM21
```

The fake emits v2 hello and 20 Hz telemetry with the simulation flag set and
supports ping. It does not simulate configurable rates or OLED commands;
unsupported commands are reported on stderr. Serial-open failures surface
explicitly. Real gateway open failures retry with capped exponential backoff.

## UNO firmware

Requires Arduino CLI and the `arduino:avr` core (tested with core 1.8.6).
No third-party library is needed; color access uses source-verified register
operations with bounded software I2C.

```powershell
$build = Join-Path $env:TEMP 'xarm-uno-build'
arduino-cli compile --fqbn arduino:avr:uno --warnings all --build-path $build .\firmware\sensor-publisher
# Explicit action: only upload after checking the wiring and chosen port.
arduino-cli upload --fqbn arduino:avr:uno --port COM5 --input-dir $build --verify .\firmware\sensor-publisher
```

Do not connect the UNO to the arm controller. Remove/disable any Bluetooth
module sharing pins D0/D1 before USB serial use. Color software-I2C needs
the appropriate external pullups already provided by the kit wiring/module.
The optional OLED is not initialized, and D commands are rejected.

To compile a clearly flagged fake-sensor firmware without accessing sensors:

```powershell
arduino-cli compile --fqbn arduino:avr:uno --build-property "compiler.cpp.extra_flags=-DSENSOR_PUBLISHER_FAKE=1" .\firmware\sensor-publisher
```

All data lines use 115200 baud and XOR checksums. The only implemented
commands are ping (`P*50` + newline) and a checksummed `C,hz,1..50`.
Use `Robot.Sensors.Protocol.Encode` to construct commands. Ping returns
firmware/reset information, capability bits, command rejects, TX drops and
maximum observed loop time. Protocol v2 is incompatible with draft-v1 hello.
Reset cause 255 means unknown: the stock bootloader may clear MCUSR before
the application starts. It is not a set of reset flags.

## Live acceptance test

Close the dashboard and serial monitors first. The following test opens the
selected board, which can reset it, checks 20/50 Hz telemetry, ping, malformed
and partial commands, and captures ten minutes at 20 Hz. It restores 20 Hz
before closing the port and saves raw serial data and a JSON summary in an
existing output directory:

```powershell
.\firmware\tests\Test-LiveFirmware.ps1 -Port COM4 -CaptureSeconds 600 `
  -OutputDirectory $env:TEMP
```

The test intentionally sends five bad commands; `CommandsRejected` remains
set until reset. That status is expected, not a sensor fault. Startup bytes
before the three-second warmup are excluded. It checks observed maximum loop
time, not electrical timing or worst-case interrupt latency. Manual stimulation,
sensor unplug/recovery, and watchdog fault injection are separate tests.

## Validation and limits

The C# tests cover malformed/checksummed input, bounds, partial/multiple lines,
resynchronization, sequence wrap/gaps, connection sharing, retry, silence,
recovery and disposal. Native C++ tests execute the actual firmware protocol
and sensor headers against fake UART/GPIO/I2C/clock adapters, including short
reads, range failure/recovery, debounce, sound peaks and a stuck software-I2C
clock deadline. With MSVC developer tools:

```powershell
cl /nologo /EHsc /std:c++17 /Ifirmware\tests /Ifirmware\sensor-publisher `
  firmware\tests\protocol-tests.cpp /Fo"$env:TEMP\xarm-protocol-tests.obj" `
  /Fe"$env:TEMP\xarm-protocol-tests.exe"
& "$env:TEMP\xarm-protocol-tests.exe"
```

Software validation on 2026-11-14:

| Check | Result |
| --- | --- |
| .NET tests | 24 passed |
| Dashboard build | 0 warnings, 0 errors |
| WPF dashboard | 9 presentation/UI tests passed; standalone window responsive and closed normally |
| Native firmware tests | Passed |
| Real-sensor UNO build | 10,458 bytes flash; 842/2048 bytes SRAM (1206 bytes free for stack/locals) |
| Fake-sensor UNO build | 6376 bytes flash; 716/2048 bytes SRAM (1332 bytes free for stack/locals) |
| In-process dashboard smoke test | 175 frames over 8755 ms device uptime: 19.99 Hz; no stderr |

Live-board acceptance on 2026-11-14, Arduino Uno/ATmega328P on COM4:

| Check | Result |
| --- | --- |
| Upload | Final real-sensor binary uploaded with flash read-back verification |
| 600-second capture | 11,850 telemetry frames; 19.792 Hz from device timestamps |
| Telemetry intervals | 50..52 ms during the ten-minute capture |
| Integrity | 0 malformed/checksum failures, 0 sequence gaps, 0 TX drops across acceptance phases |
| Configured 50 Hz phase | 49.559 Hz; 20..22 ms intervals |
| Maximum observed main-loop time | 3652 us (3.652 ms), below the 5 ms sensor-read deadline |
| Command robustness | Five intentionally malformed/invalid commands counted; partial-command/CRLF resynchronization passed |
| Color | APDS9960 detected, RGB and clear available; no observed color fault flags |
| Range | 236..499 mm during the final ten-minute capture; no observed range fault flags |
| Analog observations | Light 549..701; sound window peaks 0..44 during final capture |
| Reset cause | 255 (unknown): stock bootloader clears the original flags |
| C# dashboard against COM4 | 140 frames in the final smoke check, live readings and Good sensor status; no stderr |

Hardware testing found and fixed diagnostic hellos colliding with telemetry
deadlines and an early reset hook removed by link-time optimization. The
final compiled ELF was inspected to verify that the startup hook is retained.
Opening COM4 can first deliver buffered pre-reset frames, followed by the
UNO's DTR-triggered reboot. The gateway reports that startup sequence
discontinuity; the gap-free capture above excludes its three-second warmup.
Early pings during this boot transition can also affect command counters.

Firmware build sizes and physical test results are different evidence. These
results are observed conditions, not worst-case timing guarantees. No physical
touch/IR edge was observed in the automated capture. Manual touch/obstacle
stimulation, sensor unplug/recovery, watchdog fault injection, and a
virtual-COM-driver test remain outstanding. OLED output remains disabled.
Digital/analog unplugging cannot reliably be diagnosed from raw levels alone.
UART contention can drop whole frames/events and is explicitly counted.
This is not a safety-certified control or emergency-stop channel.
