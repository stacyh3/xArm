# Copilot instructions: xArm UNO sensor publisher and PC controller

Place this file at `.github/copilot-instructions.md`.

## Project

A Hiwonder xArm UNO robot arm with an Arduino UNO R3 sensor kit, controlled by a C# app on a PC that will host an AI agent. This repo currently covers two things:

1. **Arduino firmware** that reads the sensor kit and streams readings to the PC over USB serial.
2. **C# PC-side code** that parses that stream into Reactive Extensions observables and, later, drives the arm.

The source of truth for design decisions and the wire protocol is `docs/approach.md`. Read it before changing anything. If code and that document disagree, stop and ask; do not silently pick one.

## Sources of truth

- Design, protocol, sampling rates, task order: `docs/approach.md`.
- Sensor pins, buses, libraries and calls: the vendor material in `docs/xArm UNO Secondary Development Kit/` and the table in `docs/sensor-map.md`.
- **Never guess** a pin number, I2C address, library name or sensor call. If the vendor examples do not cover it, stop and ask a human. When stopping to ask, make no file changes for that item, list the missing item (pin, address, library, or call) with the file you checked, and wait for a reply before continuing.

## Architecture rules

- The Arduino only **reports**. It never decides, never thresholds into actions, and never sends anything to the arm.
- The arm is a separate device on a separate link. It may enumerate as a USB HID device rather than a COM port. Do not assume a serial port for it; the link type is an open item in `docs/approach.md`.
- Safety reflexes live on the PC, not in the firmware.

## Firmware rules (`firmware/sensor-publisher/`)

- Target: Arduino UNO R3 (ATmega328P, 2 KB SRAM). Report SRAM use after each build.
- No `delay()`. All timing uses `millis()` and a small scheduler; every sensor read must complete or abort within `SENSOR_READ_TIMEOUT_MS` milliseconds, defined in `config.h` (set it to 5 ms unless `docs/sensor-map.md` specifies otherwise).
- No `String` class. Use fixed buffers and `F()` for string literals.
- No floating-point printing. Publish integers: millimetres for distance, raw counts for analog and color values.
- The serial port is the data channel. **Do not print debug text to `Serial`.**
- One sensor per small class with `begin()`, `poll(now)`, `latest()` and a status flag. A failing sensor sets its status bit; it must never report a stale value as a good one.
- Enable the AVR watchdog and report the reset cause in the hello message.
- Keep pins, rates, baud and protocol version in `config.h` only.

## Wire protocol (summary; the full spec is in `docs/approach.md`, section 4)

- ASCII, one message per line, newline terminated, `*XX` hex checksum over everything before the asterisk.
- Arduino to PC: `H` (hello), `S` (periodic telemetry, also the heartbeat), `E` (immediate event on a fast digital change).
- PC to Arduino: `P` (ping), `C` (config), `D` (optional OLED text).
- Unknown or malformed input is ignored and counted, never acted on.
- Do not add, remove or reorder fields without updating `docs/approach.md` in the same change.

## C# rules (`pc/`)

- Target current .NET. Nullable reference types on, warnings as errors.
- Parsing and framing are pure functions with no I/O, so they can be unit tested.
- `SensorGateway` exposes `IObservable<SensorFrame>` (hot, reconnecting, via `Publish().RefCount()`), plus separate observables for events and health. On disconnect, retry the port open after 1 s, doubling the interval after each failed attempt up to 16 s. Emit a health event on each failed attempt. Do not stop retrying unless the app shuts down.
- Parser behavior: reject bad checksums, resync at the next newline, count rejects, and detect gaps in the sequence number.
- A silence timeout on the stream is a first-class health signal, not an exception to swallow.
- Use Reactive Extensions for sensor streams. Use TPL Dataflow for ordered, bounded command queues to the arm, with one writer per link.
- Unit tests must cover garbage input, partial lines, lines longer than `MAX_LINE_LENGTH` and bad checksums. `MAX_LINE_LENGTH` is 128 bytes.
- Provide a **fake Arduino** (virtual serial pair emitting the real protocol) so the PC code can be developed without hardware.

## Workflow

Follow the task order in `docs/approach.md`, section 11. In short:

1. Fill in `docs/sensor-map.md` from the vendor examples and get human confirmation.
2. Firmware skeleton with fake sensor values.
3. Add real sensors: digital, then analog, then I2C, then the OLED. Verify rate and unplugged-sensor behavior after each.
4. C# parser and fake Arduino with tests.
5. `SensorGateway` with reconnect and silence timeout.
6. Resolve the arm link, then spike the arm driver.

Make small, reviewable changes. For firmware changes, state the measured loop time and SRAM use in the PR description.

## When unsure

Ask. A wrong pin or wrong I2C address can silently produce plausible but wrong readings, which is worse than stopping.
