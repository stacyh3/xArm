# Arduino Sensor Publisher: Approach

Implementation baseline and task status, updated 2026-11-14. The lesson-derived
sensor map was accepted, the real firmware was uploaded and flash-verified,
and a ten-minute live telemetry capture passed. The Rx gateway and WPF
dashboard are implemented. Manual sensor stimulation, unplug/recovery and
worst-case timing checks remain incomplete.

For the next development session, read [status.md](./status.md) for the
handoff, recorded verification results, known limitations and next tasks.

## 1. Goal and scope

The Arduino UNO R3 in the xArm UNO sensor kit is a **dumb, fast, reliable sensor publisher**. It reads the kit's sensors and streams the readings to the PC over its USB serial link (UART A). The implemented PC code receives and displays sensor data. The planned agentic controller will talk to the arm separately (over a separate link from the Arduino; see section 2); it is not implemented yet.

In scope: sensor sampling, framing, integrity checks, a small command set from the PC, and fault reporting.

Out of scope: any control logic, anything that drives the arm, and calibration or unit interpretation beyond the basics. The Arduino never decides anything; it reports.

## 2. Hardware inventory (from the product page)

- **Arm:** xArm 1S, 6 DOF (5 DOF + gripper), LX-15D and LX-225 serial bus servos with position, voltage and temperature feedback. Its own bus servo controller connects over USB or Bluetooth. Payload is roughly 450 g. Powered by a 7.5 V 6 A adapter.
- **Sensor kit:** UNO R3 plus UNO expansion board, glowy ultrasonic sensor, color sensor, light sensor, sound sensor, touch sensor, 3 IR obstacle avoidance sensors, and an OLED screen.
- The UNO is **not** connected to the arm controller in our design. They are independent devices on independent links, which keeps failure domains separate.

### Arm servo IDs and joints

| Servo ID | Joint name | Motion | Notes |
| --- | --- | --- | --- |
| 1 | Gripper | Open and close the claw | Can stall against an object, so use a narrower safe range |
| 2 | Wrist roll | Rotates the gripper about its own axis | |
| 3 | Wrist pitch | Tilts the gripper up and down | |
| 4 | Elbow | Bends the forearm relative to the upper arm | |
| 5 | Shoulder | Lifts the whole arm forward and back | Carries the most load |
| 6 | Base | Rotates the whole arm left and right | |

### Verified lesson hardware

The kit-specific examples are in
[`xArm UNO Secondary Development Kit/`](./xArm%20UNO%20Secondary%20Development%20Kit/),
not just the separate library collection. See [sensor-map.md](./sensor-map.md)
for source links, access examples and driver limitations.

| Peripheral | Vendor example wiring/interface | Access |
| --- | --- | --- |
| Touch | D12, digital input, active LOW | `digitalRead(12)`; LOW means touched |
| IR obstacle x3 | D2 left, D4 center, D10 right; active LOW | `digitalRead(pin)`; LOW means detected |
| Light | A2, analog input | `analogRead(A2)`; publish raw 0..1023, not the lesson's inverted brightness percentage |
| Sound | A0, analog input | `analogRead(A0)`; publish raw counts/envelope, not the lesson's percentage |
| Glowy ultrasonic | Hardware I2C, 7-bit address `0x77`; distance registers 0 and 1 | Vendor `Ultrasound::GetDistance()` returns millimetres; use checked two-byte reads instead of its unchecked result |
| Color | Software I2C, SDA D6, SCL D7 | `ColorSensorCompat::begin()`, `dataReady()`, `readRawData(&r, &g, &b, &c)` |
| OLED | SSD1306 128x64, hardware I2C, no reset pin in example | `U8G2_SSD1306_128X64_NONAME_1_HW_I2C`; lesson rotates display 180 degrees |

On the UNO R3, hardware I2C uses A4/SDA and A5/SCL; OLED and
ultrasonic share that bus. Color uses a separate software bus. The OLED lesson
does not explicitly set an address: retain the verified constructor's library
default rather than inventing a numeric address, and confirm on hardware.

The color lesson supports **two alternatives on the same cable**: LTR381 at
`0x53` (probed first, part ID `0xC2`) or APDS9960 at `0x39`. LTR381 is
configured for a 100 ms conversion period and 3x gain. Its compatibility
driver publishes the low 16 bits of RGB and sets clear to zero because this
module has no clear channel. Zero clear is therefore not a fault by itself;
the detected model must be known before interpreting that field. There is
no separate model field in the protocol: the hello capability mask advertises
whether clear is available, and that capability is reannounced after detection.

Do not copy the lesson control loops into the publisher. They drive the arm
through SoftwareSerial D8/D9, wait with `delay()`, and print debugging text.
None of those behaviors belongs in this firmware. The color compatibility
driver also needs explicit read/write error propagation and asynchronous
startup; the ultrasonic convenience method currently ignores read failures.

**Arm connection (open item).** The Arduino shows up on the PC as a COM port, but the xArm 1S controller usually does not. Reports from other xArm and LeArm users say it enumerates as a USB HID - human interface device - (vendor ID 0x0483, product ID 0x5750), so look under Human Interface Devices in Device Manager rather than Ports. Three ways to reach it from C#: 
* Talk HID directly (for example with the HidSharp library) using Hiwonder’s bus servo controller protocol;
* Use the controller’s TTL serial header through a USB-to-TTL adapter, which gives a real COM port;
* Bluetooth. If it is not listed at all, check for a charge-only USB cable and that the 7.5 V supply is connected. This choice only affects the PC-side ArmDriver, not the Arduino firmware, so the sensor work can proceed independently.

## 3. Key design decisions

**D1. The Arduino only reports.** No arm commands, no thresholds that trigger actions. Reflexes (for example stop on close obstacle) live on the PC where they can be logged, tested and changed without reflashing.

**D2. ASCII line protocol, not binary.** One message per line, comma-separated, with a checksum. At 20 to 50 Hz and about 60 bytes per frame this is a few KB/s, far below what 115200 baud carries (about 11.5 KB/s). ASCII is debuggable in any serial monitor and trivial to parse in C#. We can move to binary later if bandwidth ever matters.

**D3. Two message kinds: telemetry frames and events.**

- A periodic **telemetry frame** (`S`) carries the latest value of every sensor. It doubles as the heartbeat, so the PC can detect silence.
- An immediate **event** (`E`) is sent the moment a fast digital input changes (IR obstacle, touch). This gives the reflex path low latency without raising the telemetry rate.

**D4. Per-sensor sampling schedule, fully non-blocking.** No `delay()`, no blocking waits. A `millis()`-based scheduler runs each sensor at its own rate. Slow sensors (ultrasonic echo wait, color integration time) never stall fast ones.

**D5. Raw values, minimal conversion.** Publish integers only: distance in millimetres, raw ADC counts for light and sound, raw RGBC counts for color. Calibration and scaling live on the PC. No floating-point printing, no `String` class, `F()` for literals, to keep SRAM (2 KB) safe.

**D6. Integrity and resync.** Telemetry and events share a uint32 sequence number
and carry a uint32 millisecond timestamp. All messages have an XOR checksum.
The PC resyncs after corruption by discarding to the next newline. Gaps in
sequence numbers tell the PC about dropped frames, including a rejected frame.

**D7. Fault visibility.** A status bitmask in each frame flags sensors that failed to init, timed out, or returned out-of-range values. A sensor failing never silently turns into a plausible-looking number.

**D8. Recoverability.** The firmware enables a 1 s AVR hardware watchdog.
Hello reports reset flags when available, or 255 (unknown) when the bootloader
cleared them. Watchdog fault injection is not yet tested on hardware.

**D9. One UART, one purpose.** Serial is the data channel, so no debug prints.
Diagnostics go through status flags and hello counters. There is no `L`
message type.

## 4. Wire protocol version 2

All messages are printable ASCII, LF terminated (CRLF is accepted), with
`*XX` as two hex digits containing the bytewise XOR of everything before the
asterisk. There is no seed or final XOR. The maximum line length is 128 bytes,
including checksum suffix but excluding CR/LF. Embedded control bytes are
rejected; readers discard an oversized line through its next LF.

**Arduino to PC**

- Hello, sent at boot, on ping/config and capability changes:
  `H,2,<fw_ver>,<reset_cause>,<sensor_mask>,<cmd_rejects>,<tx_drops>,<max_loop_us>*XX`
- Telemetry: `S,<seq>,<t_ms>,<range_mm>,<ir_bits>,<touch>,<light>,<sound>,<r>,<g>,<b>,<clear>,<status>*XX`
- Event: `E,<seq>,<t_ms>,<source>,<value>*XX`

`seq` and `t_ms` wrap modulo 2^32. Sequence is shared across S and E and advances
on each attempted emission, even if a busy transmitter drops it. Hello has no
sequence and must not hide a gap when it answers a ping. On a new connection,
the PC resets its sequence tracker. A reboot on an otherwise open connection
can also appear as a sequence discontinuity; the hello reset information and
uptime help diagnose it.

`ir_bits` is exactly three characters in left/center/right order; `010` means
only the center detector is active. Digital values and event values are
normalized to 1 = active, irrespective of active-LOW wiring. Event sources
are `IR0`, `IR1`, `IR2`, and `TOUCH`. `touch` is 0 or 1. Light and sound
are 0..1023, and range/RGBC are 0..65535. Other integers are decimal:
reset cause and capability mask are uint8, status is uint16, diagnostics are
uint32. Invalid fields, extra fields and unsupported versions are rejected.

Capability bits: 0 range, 1 IR group, 2 touch, 3 light, 4 sound, 5 RGB,
6 clear. The mask describes supported channels, not current sensor health.
Only APDS9960 advertises clear; LTR381 and the fake use clear = 0 without
that capability. Early boot can advertise RGB before the color model is
known, with the color fault flag set.

Status bits: 0 range fault, 1 color fault, 2 simulated data, 3 one or more
transmit drops since boot, 4 one or more command rejects since boot. Other
bits are reserved. Faulted I2C readings are zeroed, never reported as good.
An unplugged passive digital/analog module cannot be reliably identified
from its raw signal alone; no unsupported fault detection is claimed.

**PC to Arduino (minimal)**

- `P*XX` ping, answered with hello
- `C,hz,<value>*XX` selects an integer telemetry rate from 1 through 50 Hz,
  returning hello on acceptance. The scheduler uses `1000 / hz` whole
  milliseconds, so non-divisor rates are approximate.
- `D,<text>*XX` is reserved for optional OLED output and is **not implemented**;
  it is rejected and counted. No display address is guessed.

Unknown or malformed input is ignored and counted, never acted on. Example
ping: `P*50` followed by LF. Send `C,hz,20` with its computed checksum, not
bare text. No command controls the arm.

## 5. Implemented sampling targets

| Sensor | Type | Rate | Notes |
| --- | --- | --- | --- |
| IR obstacle (x3) | digital D2/D4/D10, active LOW | 200 Hz poll | edge-triggered events, debounced |
| Touch | digital D12, active LOW | 200 Hz poll | edge-triggered events |
| Ultrasonic | hardware I2C, `0x77` | 20 Hz | bounded bus reads, check byte count and timeout |
| Light | analog A2 | 20 Hz | light smoothing on the Arduino is optional |
| Sound | analog A0 | 1 ms polling target, 50 Hz output | peak raw ADC count over each 20 ms window; I2C can reduce sample count |
| Color | software I2C D6/D7, `0x53` or `0x39` | 10 Hz | integration time sets the ceiling |
| Telemetry frame | n/a | 20 Hz default | configurable up to 50 Hz |

## 6. Firmware structure

The sketch is split so each concern can be reasoned about and tested alone:

- `config.h`: pins, rates, baud, protocol version
- `sensors`: one small class per sensor with `begin()`, `poll(now)`, `latest()`, and a status flag
- `scheduler`: tiny millis-based task runner, no blocking calls
- `protocol`: frame builder with checksum, plus a line-based command parser with a fixed buffer
- `main`: wires them together, feeds the watchdog

The implemented build leaves OLED output disabled: `D` is optional and its
actual address still needs confirmation. No display library/framebuffer is
linked. This keeps sensor polling independent of graphics transfers.

Color access uses a small open-drain software-I2C adapter with an overall
5 ms deadline and clock-stretch checking, rather than the vendor drivers'
blocking startup/debug prints and discarded errors. The register operations
and channel ordering come from the supplied drivers. Each initialization
step performs at most one transaction and schedules the conversion wait.
LTR381 conversions follow the vendor's continuous 100 ms configuration;
a successful bus read is not proof of a new conversion. No undocumented
ready register is invented.

Ultrasonic hardware-I2C transactions have 2 ms timeouts each (write pointer,
read two bytes), and the combined operation is checked against 5 ms.
Only one slow bus is polled per main-loop pass. The final live capture
reported a maximum observed main-loop duration of 3652 us (3.652 ms).
This is an observed run, not a worst-case guarantee; per-sensor deadlines
under faults and digital sampling/event jitter still need bench measurement.

Transmit is buffered and pumped using available UART space, never blocking
on a full UART. There is one pending frame; contention drops whole attempted
frames, increments tx_drops and leaves a visible sequence gap. Simultaneous
events can therefore be dropped; this is telemetry, not a certified stop
channel. Heartbeats subsequently carry the current digital levels.
Diagnostic hellos are scheduled after telemetry, within the first half of
the telemetry interval, rather than taking the transmit slot at its deadline.
This avoids hello/config replies dropping an otherwise due telemetry frame.
AVR watchdog timeout is 1 s. The hello reset cause reports MCUSR when still
available, or 255 (unknown) if the bootloader cleared it. Stock UNO Optiboot
does not provide a reliable saved-r2 handoff; do not interpret an arbitrary
register value as reset flags. Recovering the original cause requires a
verified bootloader handoff and is not claimed for this board.

## 7. Verification status

Recorded results from 2026-11-14; these are not a new test run on every
documentation update. See [README validation results](../README.md#validation-and-limits)
and [status.md](./status.md) for details and reproducible commands.

| Verification | Status and evidence |
| --- | --- |
| Sensor source map | Complete: source-linked pins, buses and access examples; accepted for implementation |
| Real firmware build/upload | Complete: 10,458 bytes flash; 842/2048 bytes SRAM; uploaded to detected Uno on COM4 with flash read-back verification |
| Ten-minute live stream | Passed: 11,850 S frames, 19.792 Hz, 50..52 ms intervals; zero malformed/checksum failures, sequence gaps or TX drops |
| Configurable telemetry and commands | Passed live: 49.559 Hz at configured 50 Hz, ping, invalid/oversized commands and partial-command/CRLF handling; five injected rejects counted |
| Sensor readings | Range and APDS9960 RGB/clear observed without fault flags; light and sound changed during capture |
| Physical timing | Partial: maximum observed loop 3.652 ms; worst-case jitter and fault-condition deadlines not measured |
| Parser/framer/gateway tests | Passed: 24 C# tests covering malformed data, bounds, resynchronization, sequence handling, shared connections, retry, silence/recovery and disposal |
| Native firmware tests | Passed: actual protocol/sensor headers exercised with fake UART, GPIO, I2C and clock |
| WPF dashboard | Passed: 9 presentation/STA UI tests with fake data, binding checks, disconnect/reconnect; standalone executable opened and closed normally |
| Dashboard on real hardware | Terminal dashboard passed against COM4; separate WPF live-hardware acceptance remains to be recorded |
| Fake Arduino | In-process fake and serial emitter implemented; in-process dashboard measured at 19.99 Hz; virtual COM pair not tested |
| Remaining physical checks | Manual touch/IR edges, I2C unplug/recovery, LTR381 hardware, watchdog fault injection and per-sensor rates remain unverified |

## 8. How it feeds the PC architecture

The PC's SensorGateway shares a reconnecting observable using
`Publish().RefCount()`. Frames, Events, Hellos and Health derive from that
single connection. Parsing, bounded incremental framing and sequence tracking
have no I/O. Checksums and gaps are validated before dashboard deduplication.

The WPF dashboard (`pc/Robot.Dashboard.Wpf`) is the primary Windows UI.
It reuses the gateway and samples visual readings at 10 Hz, with 30-second
bounded rolling charts, a one-second sound mean via Rx `Buffer`, explicit
stale/simulation banners, sensor fault states and a 200-entry event/health
log. Health and input events bypass visual sampling. UI updates are marshaled
to the WPF dispatcher; there is one shared gateway subscription per session.
Closing or disconnecting disposes the session. A generation guard prevents
already queued UI callbacks from an old session updating a new one.
Range/color faults are not plotted as valid zero readings; chart gaps and
dimmed cards indicate stale data. Protocol warnings are separate from
connection freshness and remain visible until dismissed.

The retained terminal dashboard uses Rx `DistinctUntilChanged` on sensor values
(excluding sequence/time), a one-second sound mean via `Buffer`, and a 250 ms
refresh. It preserves all health and digital event messages. Two seconds
without valid S frames emits Silence rather than terminating or substituting
fake values. Recovery is an explicit health event. Failed connections retry
after 1, 2, 4, 8, then 16 seconds indefinitely until unsubscribed/shutdown;
receiving valid telemetry resets backoff. A failed attempt emits its cause.
Ping every five seconds refreshes device diagnostics without resetting gap
tracking. DTR is enabled: opening a real UNO port normally resets it.

No integrated arm connection, reflex, Dataflow command queue or WorldState
controller is implemented in the sensor solution. A separate experimental
[LINQPad arm tester](../linqpad/XArmControl.linq) exists, with HID/serial
transports and movement controls, but it has no recorded build or hardware
acceptance here. Its joint limits/home values are explicitly placeholders.
It does not resolve the actual arm link or qualify as a production driver.
A future arm driver must use one ordered bounded Dataflow writer per
confirmed arm link.

## 9. Open questions to confirm

- **Remaining physical validation.** Live range/APDS9960/analog readings and a ten-minute stream are verified. Manual touch/IR stimulation, unplug/recovery, bus fault deadlines and worst-case sampling jitter still need bench checks.
- **Alternative color module.** APDS9960 was detected on the tested kit (hello mask 127). LTR381 support is implemented from vendor sources but not hardware-tested.
- **OLED address.** The lesson relies on its constructor's default. Confirm the actual bus address before adding address-specific code.
- **Bluetooth on the expansion board.** The board supports Bluetooth communication, which on many UNO shields shares the hardware serial pins. If so, the Bluetooth module must be removed or disabled while using USB serial.
- **Arm link and safe joint limits.** Device identity, transport, report framing and numeric safe ranges remain unconfirmed. The section 2 joint map is documented, but the LINQPad prototype's numeric limits are not calibrated.
- **OLED library footprint.** Some OLED libraries use a large share of the UNO’s 2 KB of RAM; we may need a text-only driver.
- **Required reaction time.** Decide the target latency from obstacle event to arm stop so the event rates and PC reflex path are sized correctly.

## 10. Completed milestones and next tasks

| Milestone | Status |
| --- | --- |
| Vendor analysis and sensor map | Complete; map accepted and access examples documented |
| Firmware skeleton, scheduler and serial protocol | Complete, including explicit fake-sensor build mode |
| Digital, analog and I2C sensor implementations | Complete; physical acceptance is partial as listed in section 7 |
| Real UNO upload and stream acceptance | Complete for the recorded COM4/APDS9960 run |
| C# protocol parser, framer and fake Arduino | Implemented and tested in-process; virtual-pair acceptance outstanding |
| Rx SensorGateway | Complete with reconnect, health, silence and shared subscriptions |
| Terminal and WPF dashboards | Complete; terminal live smoke and WPF fake/UI tests recorded |
| Servo ID/joint definitions | Documented for all six joints; numeric limits unconfirmed |
| OLED output | Not implemented; optional D command is reserved and rejected |
| Integrated arm driver and PC control/agent | Not implemented; LINQPad tester is experimental only |

Next work:

1. Finish manual sensor acceptance and record WPF behavior with live hardware,
   silence, disconnection and recovery.
2. Exercise the fake serial emitter through a real virtual COM pair and
   validate physical fault timing/watchdog recovery without arm movement.
3. Resolve the actual arm link using device inspection and read-only probes.
   Audit the LINQPad tester before reusing any protocol/transport code.
4. After transport, protocol and numeric limits are confirmed, implement a
   tested arm driver with an ordered bounded Dataflow queue and one writer.
   Keep any movement tests separate and human-supervised.
5. Add OLED support only if wanted, after address confirmation and bounded
   transfer/SRAM validation. It is not a blocker for sensor telemetry.

The detailed prerequisites and acceptance criteria are in [status.md](./status.md).

## 11. Handoff notes for GitHub Copilot

This document, `docs/approach.md`, is the source of truth for the build.
Update it in the same change whenever the protocol or a decision changes.

**Current repo layout and planned addition**

```text
docs/approach.md
docs/sensor-map.md           sensor, pins, bus, library, example file (task 1)
docs/xArm UNO Secondary Development Kit/  supplied vendor material
firmware/sensor-publisher/   Arduino sketch (sections 3 to 6)
pc/Robot.Sensors/            C# SensorGateway, frame parser, fake Arduino
pc/Robot.Dashboard/          terminal dashboard and fake serial emitter
pc/Robot.Dashboard.Wpf/      graphical Windows dashboard
pc/Robot.Dashboard.Wpf.Tests/ presentation and STA window smoke tests
pc/Robot.Sensors.Tests/      parser, framing and gateway tests
linqpad/XArmControl.linq     experimental arm tester; not accepted/integrated
pc/Robot.Arm/                planned only; no project exists yet
```

**Rules for the coding agent**

1. Sensor access code must come from the supplied examples in `docs/xArm UNO Secondary Development Kit/` (the actual vendor location in this checkout). The standalone `docs/Arduino IDE Development Library/` contains supporting libraries and generic examples. Do not guess pins, I2C addresses or library calls; if the examples do not cover something, stop and ask.
2. Do not change the wire protocol (section 4) without updating this document in the same change.
3. Firmware: no `delay()`, no `String` class, no debug prints on `Serial`, and report SRAM use after each build.
4. Every message carries a checksum. The C# parser must reject bad checksums and count them.
5. The Arduino never sends commands to the arm.

**Original task order, with current disposition**

1. Vendor examples and human-confirmed sensor map: complete.
2. Firmware skeleton and fake readings: complete.
3. Digital, analog and I2C code: complete; manual rate/unplug checks incomplete.
   OLED was intentionally deferred.
4. Parser and fake Arduino: complete in-process; virtual serial-pair verification
   incomplete.
5. Reconnecting SensorGateway and silence health: complete, unit-tested.
6. Arm link and ArmDriver: actual link unresolved; prototype exists outside
   the sensor solution, and the integrated driver has not started.
