#include "config.h"
#include "protocol.h"
#include "sensors.h"
#include <avr/wdt.h>

uint8_t resetCause __attribute__((section(".noinit")));
void captureReset() __attribute__((naked, used, externally_visible, section(".init3")));
void captureReset() {
  // Stock UNO Optiboot clears MCUSR without a reliable application handoff.
  resetCause = MCUSR;
  if (!resetCause) resetCause = 255;
  MCUSR = 0;
  wdt_disable();
}

Protocol protocol;
DigitalSensor ir[] = {DigitalSensor(Config::IrPins[0]), DigitalSensor(Config::IrPins[1]), DigitalSensor(Config::IrPins[2])};
DigitalSensor touch(Config::TouchPin);
AnalogSensor light(Config::LightPin, Config::LightMs);
SoundSensor sound;
RangeSensor rangeSensor;
ColorSensor colorSensor;
uint32_t telemetryAt = 0, maxLoopUs = 0;
uint8_t announcedMask = 0;

uint8_t sensorMask() {
  // Bits: range, IR, touch, light, sound, RGB, clear.
  return 63 | ((!Config::Fake && colorSensor.model() == 2) ? 64 : 0);
}

void hello() {
  if (protocol.busy()) { protocol.helloRequested = true; return; }
  protocol.send(PSTR("H,%u,1.0,%u,%u,%lu,%lu,%lu"),
    Config::ProtocolVersion, resetCause, sensorMask(),
    protocol.rejected, protocol.dropped, maxLoopUs);
  announcedMask = sensorMask();
  protocol.helloRequested = false;
}

void event(uint32_t now, const char *source, bool value) {
  protocol.send(PSTR("E,%lu,%lu,%s,%u"), protocol.sequence++, now, source, value);
}

void setup() {
  Serial.begin(Config::Baud);
  Wire.begin();
  Wire.setWireTimeout(Config::WireTimeoutUs, true);
  if (!Config::Fake) {
    for (auto &sensor : ir) sensor.begin();
    touch.begin(); light.begin(); sound.begin(); rangeSensor.begin(); colorSensor.begin();
  }
  wdt_enable(WDTO_1S);
  hello();
}

void loop() {
  uint32_t started = micros(), now = millis();
  protocol.pump();
  uint8_t bits = 0, touched = 0;
  uint16_t rangeMm, lightValue, soundValue, status;
  ColorValue color;
  if (Config::Fake) {
    rangeMm = 250; lightValue = 512; soundValue = (now / Config::SoundMs) % 100;
    color.r = 120; color.g = 80; color.b = 60;
    status = 4;
  } else {
    const char *sources[] = {"IR0", "IR1", "IR2"};
    for (uint8_t i = 0; i < 3; ++i) {
      if (ir[i].poll(now)) event(now, sources[i], ir[i].latest());
      if (ir[i].latest()) bits |= 1 << i;
    }
    if (touch.poll(now)) event(now, "TOUCH", touch.latest());
    touched = touch.latest();
    light.poll(now); sound.poll(now);
    // Alternate slow buses, leaving a digital polling opportunity between reads.
    static bool colorTurn = false;
    if (colorTurn) colorSensor.poll(now); else rangeSensor.poll(now);
    colorTurn = !colorTurn;
    rangeMm = rangeSensor.latest(); lightValue = light.latest(); soundValue = sound.latest();
    color = colorSensor.latest();
    status = (rangeSensor.failed() ? 1 : 0) | (colorSensor.failed() ? 2 : 0);
  }
  if (protocol.dropped) status |= 8;
  if (protocol.rejected) status |= 16;
  if (due(now, telemetryAt, 1000 / protocol.hz)) {
    protocol.send(PSTR("S,%lu,%lu,%u,%u%u%u,%u,%u,%u,%u,%u,%u,%u,%u"),
      protocol.sequence++, now, rangeMm, !!(bits & 1), !!(bits & 2), !!(bits & 4),
      touched, lightValue, soundValue, color.r, color.g, color.b, color.clear, status);
  }
  // Send diagnostics away from the next telemetry deadline.
  if ((protocol.helloRequested || announcedMask != sensorMask())
      && uint32_t(now - telemetryAt) < uint16_t(500 / protocol.hz)) hello();
  uint32_t elapsed = micros() - started;
  if (elapsed > maxLoopUs) maxLoopUs = elapsed;
  wdt_reset();
}
