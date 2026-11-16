# Sensor map from the vendor lessons

Status: lesson-derived map accepted for implementation on 2026-11-14.
Physical bench validation remains outstanding. These snippets document access, not production publisher
firmware. Do not run the original lesson sketches as a sensor publisher:
several also command the arm.

## Digital inputs

| Sensor | Pin | Mode | Meaning |
| --- | --- | --- | --- |
| Touch | D12 | INPUT | LOW = touched |
| IR1, left | D2 | INPUT | LOW = obstacle |
| IR2, center | D4 | INPUT | LOW = obstacle |
| IR3, right | D10 | INPUT | LOW = obstacle |

Sources: [Touch_control.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%202%20Touch%20Control/Touch_control/Touch_control/Touch_control.ino)
and [Auto_sorting.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%206%20Auto%20Sorting/Auto_sorting/Auto_sorting.ino).

```cpp
pinMode(12, INPUT);
pinMode(2, INPUT);
pinMode(4, INPUT);
pinMode(10, INPUT);
bool touched = digitalRead(12) == LOW;
bool leftObstacle = digitalRead(2) == LOW;
bool centerObstacle = digitalRead(4) == LOW;
bool rightObstacle = digitalRead(10) == LOW;
```

Publisher implementation must put these pins in config.h, poll every 5 ms,
and debounce without waiting. An unplugged digital input cannot reliably be
distinguished from a valid level by digitalRead alone; do not claim otherwise
or invent a disconnected-sensor status.

## Analog inputs

| Sensor | Pin | Access | Output |
| --- | --- | --- | --- |
| Sound | A0 | `analogRead(A0)` | Raw ADC 0..1023 |
| Light | A2 | `analogRead(A2)` | Raw ADC 0..1023 |

Sources: [Sound_control.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%205%20Intelligent%20Sound%20Control/Sound_control/Sound_control.ino)
and [Photosensitive.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%209%20Light%20Sensitivity/Photosensitive/Photosensitive.ino).

```cpp
pinMode(A0, INPUT);
pinMode(A2, INPUT);
uint16_t sound = analogRead(A0);
uint16_t light = analogRead(A2);
```

The light lesson maps 0..1023 to 100..0 brightness; the sound lesson maps
0..1023 to 0..100 volume. Neither is calibrated physical-unit data. Keep raw
counts on the wire and perform aggregation in PC-side Rx. Floating analog
inputs cannot be diagnosed as unplugged using ADC range checks alone.

## Glowy ultrasonic

Sources: [Ultrasound.h](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%207%20Distance%20Measurement/Distance/Ultrasound.h),
[Ultrasound.cpp](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%207%20Distance%20Measurement/Distance/Ultrasound.cpp)
and [Distance.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%207%20Distance%20Measurement/Distance/Distance.ino).

- Hardware I2C through Wire, UNO R3 A4/SDA and A5/SCL.
- Seven-bit address 0x77.
- Register 0: distance low byte; register 1: high byte; unit millimetres.
- Vendor class: Ultrasound; convenience read: GetDistance().

```cpp
#include "Ultrasound.h"
Ultrasound ultrasound;
uint8_t bytes[2] = {};
int count = ultrasound.wireReadDataArray(0x77, 0, bytes, 2);
bool valid = count == 2;
if (valid) {
    uint16_t rangeMm = uint16_t(bytes[0]) | (uint16_t(bytes[1]) << 8);
    // Store rangeMm only after this successful read.
}
```

This illustrates the checked vendor API, not a bounded production read.
GetDistance() ignores the read result and can return uninitialized data after
a failed transaction. The publisher must impose an overall 5 ms read deadline
and bus timeouts, flag failed/short reads, and never mark stale range as good.
Do not copy the lesson's RGB animations or distance-triggered actions.
The supplied source does not establish a guaranteed valid distance range.

## Color

Sources: [Discriminate_color.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%2010%20Color%20Identify/Discriminate_color/Discriminate_color.ino),
[ColorSensorCompat.h](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%2010%20Color%20Identify/Discriminate_color/ColorSensorCompat.h)
and [ColorSensorCompat.cpp](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%2010%20Color%20Identify/Discriminate_color/ColorSensorCompat.cpp).

- Software I2C through SoftWire: SDA D6, SCL D7.
- LTR381: address 0x53; part ID register 0x06 must contain 0xC2.
  The compatibility driver probes this model first. It writes control 0x00 =
  0x06, measurement rate 0x04 = 0x42, and gain 0x05 = 0x03.
- LTR381 RGB registers: green 0x0D, red 0x10, blue 0x13. The supplied
  implementation reads three bytes/channel and retains the low 16 bits.
  It reports clear = 0; there is no clear channel.
- APDS9960 fallback: address 0x39, begin() then enableColor(true).
  Its library provides colorDataReady() and getColorData().

```cpp
#include "SoftWire.h"
#include "ColorSensorCompat.h"
SoftWire sw(6, 7);
ColorSensorCompat colorSensor(sw);

// Initialization:
bool initialized = colorSensor.begin();
// On a later scheduled poll, only when initialized:
if (initialized && colorSensor.dataReady()) {
    uint16_t r, g, b, clear;
    colorSensor.readRawData(&r, &g, &b, &clear);
}
```

These are the verified vendor calls, not a safe production adapter.
The compatibility layer waits 150 ms for LTR381 startup; APDS startup also
uses delays and prints to Serial. Register writes discard errors, failed
LTR381 channel reads become zero, and LTR381 dataReady() only reflects
successful initialization, not a new successful conversion. Production code
must propagate transaction failures, schedule startup asynchronously,
remove protocol-corrupting debug output, and enforce an overall read deadline
rather than assuming per-byte timeouts bound the whole operation.

The publisher probes both models and advertises clear-channel capability
in protocol-v2 hello messages; see [approach.md](./approach.md). The optional
OLED remains disabled pending address confirmation.

## OLED

Source: [Auto_sorting.ino](./xArm%20UNO%20Secondary%20Development%20Kit/Lesson%206%20Auto%20Sorting/Auto_sorting/Auto_sorting.ino).

- SSD1306 128x64, hardware I2C shared with ultrasonic.
- The lesson specifies no reset pin and uses a one-page graphics buffer.
- The lesson does not explicitly set the I2C address; do not infer a numeric
  address as a confirmed kit fact.

```cpp
#include "U8g2lib.h"
U8G2_SSD1306_128X64_NONAME_1_HW_I2C oled(U8G2_R0, U8X8_PIN_NONE);

// Initialization:
oled.begin();
oled.setDisplayRotation(U8G2_R2);
oled.setFont(u8g2_font_fub20_tr);
// A complete vendor-style redraw:
oled.firstPage();
do {
    oled.setCursor(0, 38);
    oled.print(F("Hiwonder"));
} while (oled.nextPage());
```

For the publisher, display work is lowest priority and must be split/bounded
so page transfers cannot stall digital polling. Confirm the actual address
before adding address-specific code; do not use a full-frame SRAM buffer.

## Physical checks before live use

1. Assembled wiring matches the map above; the Arduino has no arm-control link.
2. Fitted color model and treatment of the absent LTR381 clear channel.
3. OLED address if explicit addressing is needed, and any Bluetooth module
   sharing the USB serial UART.

The lesson files are the actual vendor source location in this checkout.
