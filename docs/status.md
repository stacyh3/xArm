# xArm implementation status and handoff

Updated: 2026-11-14.

## 1. Start here

The sensor-monitoring milestone is implemented: real UNO firmware streams
protocol-v2 readings, the C# Rx gateway parses/reconnects/reports health,
and a WPF dashboard displays the data. The real firmware has been uploaded
and accepted in a ten-minute capture. This is not yet an arm controller or
a safety-certified system.

Read these before changing code:

- [approach.md](./approach.md): architecture, wire protocol and task order;
  this remains the design source of truth.
- [sensor-map.md](./sensor-map.md): source-linked sensor wiring/access and
  vendor-driver limitations.
- [README.md](../README.md): launch, build, upload and test instructions.
- [copilot-instructions.md](../.github/copilot-instructions.md): repository
  implementation rules.

This document records status and handoff, not a replacement protocol.
Results below are prior recorded checks, not newly executed tests during
this documentation update. No firmware upload or arm movement is part of
this handoff.

## 2. What is complete

| Area | Deliverable | Entry points |
| --- | --- | --- |
| Sensor mapping | Lesson-derived map accepted; pins, buses, APIs and examples documented | [sensor-map.md](./sensor-map.md) |
| Firmware | Scheduled digital/analog/I2C reads, sound-window peak, bounded bus operations, watchdog and fake mode | [sensor-publisher.ino](../firmware/sensor-publisher/sensor-publisher.ino), [sensors.h](../firmware/sensor-publisher/sensors.h), [config.h](../firmware/sensor-publisher/config.h) |
| Arduino serial protocol | XOR-framed H/S/E messages, shared sequence, bounded command input, P and C,hz support, fault/diagnostic counters | [protocol.h](../firmware/sensor-publisher/protocol.h) |
| PC framing/parsing | Pure bounded incremental framing, checksum/value rejection, typed messages and sequence tracking | [Protocol.cs](../pc/Robot.Sensors/Protocol.cs) |
| Gateway | Hot shared Rx stream, reconnect backoff, health notifications, two-second silence signal, periodic ping and cancellation/disposal | [SensorGateway.cs](../pc/Robot.Sensors/SensorGateway.cs) |
| Fake Arduino | In-process connection and real-protocol serial emitter for an externally supplied virtual pair | [Connections.cs](../pc/Robot.Sensors/Connections.cs) |
| WPF UI | Sensor cards, analog bars, 30-second charts, 10 Hz visuals, sound mean, explicit stale/simulation/fault states and bounded event log | [MainWindow.xaml](../pc/Robot.Dashboard.Wpf/MainWindow.xaml), [DashboardViewModel.cs](../pc/Robot.Dashboard.Wpf/DashboardViewModel.cs) |
| Terminal tools | Original dashboard and fake-serial command-line mode retained | [Program.cs](../pc/Robot.Dashboard/Program.cs) |
| Joint definitions | IDs 1..6: gripper, wrist roll, wrist pitch, elbow, shoulder, base | [approach hardware section](./approach.md#2-hardware-inventory-from-the-product-page) |

The WPF app is sensor-only. It does not send joint commands. All health/input
events bypass visual sampling; old-session callbacks are guarded against
updating a new connection. Graph histories and the event log are bounded.

## 3. Recorded verification baseline

### Software

- .NET 10; nullable references and warnings-as-errors enabled.
- 24 sensor/parser/framer/gateway tests passed.
- 9 WPF presentation/STA-window tests passed, including fake readings, no
  binding errors, disconnect/reconnect and bounded stale/fault presentation.
- Standalone WPF executable showed a responsive window and closed normally.
- Native C++ tests exercised the actual firmware headers with fake UART,
  GPIO, I2C and clock: checksums, command parsing, framing/resync, TX bounds,
  range failure/recovery, debounce, sound peak and a stuck-clock deadline.
- In-process terminal fake smoke: 175 frames over 8755 ms device uptime,
  19.99 Hz, no stderr.

| Firmware build | Flash | Static SRAM | Remaining SRAM for stack/locals |
| --- | --- | --- | --- |
| Real sensors | 10,458 bytes | 842/2048 bytes | 1206 bytes |
| Fake sensors | 6376 bytes | 716/2048 bytes | 1332 bytes |

### Real hardware

Tested board: detected Arduino Uno/ATmega328P on **COM4**. COM4 is a
previously observed assignment, not a guaranteed port for the next session.
The final real-sensor binary was uploaded with flash read-back verification.
The fitted color module was detected as APDS9960, with hello mask 127.

| Check | Recorded result |
| --- | --- |
| Ten-minute telemetry capture | 11,850 S frames; 19.792 Hz from device timestamps |
| Frame intervals | 50..52 ms during the capture |
| Integrity across acceptance phases | Zero malformed/checksum failures, sequence gaps and TX drops |
| Configured 50 Hz | 49.559 Hz; 20..22 ms intervals |
| Maximum observed main loop | 3652 us (3.652 ms) |
| Command checks | Ping, rate changes, five invalid/oversized commands counted, partial input/CRLF resynchronization |
| Sensor health | No observed range/color faults; range 236..499 mm, light 549..701, sound peaks 0..44 in final capture |
| Reset reporting | 255 = unknown, because the bootloader cleared the original reset flags |
| Real PC display | Terminal dashboard smoke passed with 140 frames and Good sensor status |

The automated capture observed no physical touch/IR edge. WPF was exercised
with fake data; a separate WPF real-hardware acceptance run is not recorded.
The loop metric is an observed condition, not a worst-case timing guarantee
or evidence that every sensor was independently sampled at its target rate.

Raw capture/JSON evidence was saved outside the repository in the development
session. The durable summary is in this document and the README. A fresh clone
should reproduce the test rather than assume those local files exist.

## 4. Known limitations and operational traps

- **OLED is disabled.** The D command is reserved, rejected and counted.
  Confirm the actual address before adding address-specific code.
- **LTR381 hardware is untested.** Vendor-derived support exists, but only
  APDS9960 was observed on this kit. Clear = 0 without the clear capability
  is expected for LTR381/fake data, not itself a fault.
- **USB opening can reset the UNO.** DTR is enabled. Buffered pre-reset frames
  followed by reboot can produce a startup sequence discontinuity in the
  health log. Early pings can affect command counters. The live capture
  excludes a three-second warmup; steady-state gaps must not be dismissed.
- **Malformed-command tests leave a flag set.** Five rejects are deliberately
  injected; status bit 4 stays set until reset. It is not a sensor fault.
- **TX is one pending frame.** Contention can drop whole frames or simultaneous
  digital events. Drops are counted and visible as sequence gaps; telemetry
  subsequently carries current digital levels. Do not treat E as a guaranteed
  emergency-stop channel.
- **Passive unplugging is ambiguous.** Raw digital/analog levels do not reliably
  distinguish a disconnected module from a valid measurement. Do not add
  guessed thresholds that claim otherwise.
- **Reset cause can be unavailable.** Do not interpret 255 as reset flags.
  A true cause needs a verified bootloader handoff.
- **Retain the early startup hook.** Hardware testing found that LTO removed
  an unreferenced .init3 function. The final hook uses used/externally_visible;
  inspect the compiled ELF if changing startup/watchdog code.
- **Keep replies off telemetry deadlines.** Diagnostic hello scheduling was
  corrected after a live rate-change test exposed a dropped telemetry frame.
- **One serial client at a time.** Close dashboard/Serial Monitor before
  upload or live tests. No script installs virtual COM drivers.
- **No calibrated units beyond range.** Light/sound/color are raw counts;
  graphs, averages and test ranges are not calibrated physical limits.

## 5. Arm work: existing prototype, not a completed driver

[linqpad/XArmControl.linq](../linqpad/XArmControl.linq) is an independent
LINQPad WPF tester. It contains HID via HidSharp, serial at 9600 baud,
binary LSC framing, position/battery requests, joint sliders, movement,
Home, live-drag sending and servo release.

No build, actual device/transport confirmation, movement test or acceptance
for that prototype is recorded here. Its numeric joint min/max/home values
are explicitly placeholders. The HID VID/PID and report-ID/padding assumptions
in it are hypotheses until verified against the actual controller/vendor
protocol. They do not prove that the arm link is resolved.

Do not run Home, live dragging, Send or Release as a discovery test: these
commands can move the arm or remove holding torque. Do not copy the prototype's
error handling, placeholder limits or write paths into an accepted driver
without review and tests. No integrated Robot.Arm project exists yet.

The Arduino sensor link and arm link must remain separate. The UNO publisher
never commands the arm; safety/reflex logic belongs on the PC.

## 6. Next tasks and acceptance criteria

### A. Finish sensor and live WPF acceptance

Prerequisites: assembled sensor kit, confirmed UNO port, other serial clients
closed; no arm-control connection needed.

1. Run WPF against the real UNO. Check raw sensor changes, channel capabilities,
   fault/stale banners, chart updates and the event log.
2. Stimulate touch and each IR sensor individually. Verify polarity, source
   labels, debouncing, E messages and matching S digital levels.
3. Measure per-sensor sampling and digital event jitter rather than infer
   them from telemetry rate alone.
4. Test I2C module disconnect/reconnect and stuck/failed reads. Verify fault
   flags, zeroed invalid readings, recovery and bounded loop behavior.
5. Disconnect/reconnect the UNO. Verify health events, retry, silence/staleness,
   recovery and WPF connection disposal without leaking port ownership.

Done when: stimulus/fault scenarios and measured timing are recorded, and
stale/fault data is never presented as current good data.

### B. Complete development-without-hardware and recovery checks

- Exercise the serial fake through an independently installed virtual COM pair;
  verify protocol, ping, reconnect and silence through the real serial path.
- Test watchdog recovery with controlled fault injection on the sensor board
  only; distinguish recovery from the unavailable original reset cause.
- If an LTR381 module is available, repeat color acceptance and confirm the
  missing-clear presentation.

Done when: results identify what was physically/virtually exercised versus
only tested with adapters.

### C. Resolve the arm transport before movement

This read-only discovery can proceed separately from A/B.

1. Read the vendor controller protocol and inspect the actual device identity,
   interfaces/report descriptors or confirmed serial header.
2. Confirm transport, framing, report sizes/IDs and response parsing. Use
   documented read-only position/battery requests only after protocol confirmation.
3. Record verified facts in approach.md. Review the LINQPad prototype as prior
   art, not a source of verified numeric limits or transport assumptions.

Done when: the actual link and request/response shapes are reproducible and
documented, with no guessed sensor pins/addresses or automatic movement.

### D. Implement the accepted arm driver

Prerequisites: C; human-confirmed safe numeric joint limits and supervised
movement procedure before any motion test.

- Create the PC arm project with pure protocol/framing tests and a fake link.
- Use one ordered bounded TPL Dataflow command queue/writer per arm link.
- Surface transport errors, disconnects and feedback health explicitly.
- Validate ranges and command timing; do not promote placeholder Home values.
- Keep AI/WorldState/reflex integration as later work after driver acceptance.

Done when: the tested driver honors confirmed transport/limits, produces
ordered bounded writes and passes supervised acceptance without changing the
UNO's report-only role.

### E. Optional OLED

Only if requested: confirm controller/address, implement bounded low-priority
transfers without a full-frame SRAM buffer, define D validation/behavior in
approach.md, rebuild with SRAM report and recheck timing. This is not required
to complete sensor monitoring or read-only arm discovery.

## 7. Resume commands

Run from the repository root in PowerShell. Requires .NET 10 SDK; WPF needs
Windows. The SDK rolls forward within .NET 10 as defined in
[global.json](../global.json).

```powershell
# Unit/presentation tests; WPF suite includes a real-window STA test with fake data:
dotnet test .\pc\Robot.Sensors.Tests\Robot.Sensors.Tests.csproj
dotnet test .\pc\Robot.Dashboard.Wpf.Tests\Robot.Dashboard.Wpf.Tests.csproj

# No-hardware graphical run:
dotnet run --project .\pc\Robot.Dashboard.Wpf -- --fake

# Discover the current UNO port, then substitute it below:
dotnet run --project .\pc\Robot.Dashboard -- --ports
dotnet run --project .\pc\Robot.Dashboard.Wpf -- COM4
```

The following CLI executable was available locally during acceptance; it
may not be on PATH. Check the installed Arduino tooling on the next machine:

```powershell
$cli = Join-Path $env:LOCALAPPDATA 'Programs\arduino-ide\resources\app\lib\backend\resources\arduino-cli.exe'
& $cli board list
$build = Join-Path $env:TEMP 'xarm-uno-build'
& $cli compile --fqbn arduino:avr:uno --warnings all --build-path $build .\firmware\sensor-publisher
# Only upload after identifying the board and closing serial clients:
& $cli upload --fqbn arduino:avr:uno --port COM4 --input-dir $build --verify .\firmware\sensor-publisher

# Opens/resets the selected UNO, runs command checks, restores 20 Hz on exit:
.\firmware\tests\Test-LiveFirmware.ps1 -Port COM4 -CaptureSeconds 600 `
  -OutputDirectory $env:TEMP
```

Arduino core 1.8.6 was used for recorded builds. Native C++ header-test
instructions are in [README.md](../README.md#validation-and-limits).

## 8. Handoff discipline

- Check the working tree before edits; the observed checkout had untracked
  source/documentation and prototype files. Do not discard or overwrite them.
- Read vendor evidence before changing pins, addresses, libraries or calls.
  Ask rather than guess when evidence is missing.
- Update approach.md with any protocol/design change; update this document
  when acceptance status changes. Keep README launch instructions consistent.
- Record build SRAM and measured timing after firmware changes. Record which
  board/color model/transport was actually tested.
- Preserve pure parsing/framing, Rx health signaling, bounded histories and
  the independent Arduino/arm links.
