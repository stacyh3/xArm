#include <cassert>
#include <iostream>
#include "protocol.h"
#include "sensors.h"

FakeSerial Serial;
FakeWire Wire;
uint32_t clockUs = 0;
int pinValues[20] = {};
uint16_t adcValue = 0;

std::string encode(const std::string &payload) {
  uint8_t checksum = 0;
  for (char value : payload) checksum ^= static_cast<uint8_t>(value);
  char suffix[5];
  snprintf(suffix, sizeof(suffix), "*%02X\n", checksum);
  return payload + suffix;
}

void receive(Protocol &protocol, const std::string &wire) {
  for (char value : wire) Serial.input.push_back(value);
  while (Serial.available()) protocol.pump();
}

int main() {
  Protocol protocol;
  receive(protocol, "P*50\n");
  assert(protocol.helloRequested && protocol.rejected == 0);
  protocol.helloRequested = false;
  receive(protocol, encode("C,hz,50"));
  assert(protocol.hz == 50 && protocol.helloRequested);
  receive(protocol, encode("C,hz,0") + encode("C,hz,51") + encode("C,hz,9999999999"));
  assert(protocol.hz == 50 && protocol.rejected == 3);
  receive(protocol, encode("C,hz,-1") + encode("C,hz,") + encode("C,hz, 2") +
    encode("D,test") + "P*00\nP*GG\n");
  assert(protocol.rejected == 9);
  receive(protocol, std::string(4096, 'x') + "\n" + encode("C,hz,20"));
  assert(protocol.rejected == 10 && protocol.hz == 20);
  protocol.helloRequested = false;
  receive(protocol, "P*50\r\n");
  assert(protocol.helloRequested);
  protocol.helloRequested = false;
  receive(protocol, "P*");
  assert(!protocol.helloRequested);
  receive(protocol, "50\n");
  assert(protocol.helloRequested);
  assert(protocol.send(PSTR("S,%lu,%lu,%u,010,0,611,47,120,80,60,310,0"), 1042UL, 52310UL, 184));
  assert(!protocol.send(PSTR("P")));
  assert(protocol.dropped == 1);
  while (protocol.busy()) protocol.pump();
  assert(Serial.output == encode("S,1042,52310,184,010,0,611,47,120,80,60,310,0"));
  Serial.output.clear();
  std::string maximum(125, 'x');
  assert(protocol.send(PSTR("%s"), maximum.c_str()));
  while (protocol.busy()) protocol.pump();
  assert(Serial.output.size() == 129 && Serial.output == encode(maximum));
  assert(!protocol.send(PSTR("%s"), std::string(126, 'x').c_str()));
  assert(protocol.dropped == 2);
  receive(protocol, encode(maximum).substr(0, 128) + "\r\n" + encode("C,hz,10"));
  assert(protocol.rejected == 11 && protocol.hz == 10);

  RangeSensor range;
  range.begin();
  range.poll(50);
  assert(!range.failed() && range.latest() == 0x1234);
  Wire.count = 1;
  range.poll(100);
  assert(range.failed() && range.latest() == 0);
  Wire.count = 2;
  range.poll(150);
  assert(!range.failed() && range.latest() == 0x1234);
  Wire.transmissionError = 2;
  range.poll(200);
  assert(range.failed() && range.latest() == 0);

  DigitalSensor digital(Config::TouchPin);
  digital.begin();
  assert(digital.latest());
  pinValues[Config::TouchPin] = HIGH;
  assert(!digital.poll(5));
  assert(!digital.poll(10));
  assert(digital.poll(15) && !digital.latest());

  SoundSensor sound;
  sound.begin();
  adcValue = 800; sound.poll(1);
  adcValue = 100; sound.poll(20);
  assert(sound.latest() == 800);
  sound.poll(40);
  assert(sound.latest() == 100);

  ColorBus colorBus;
  colorBus.begin();
  uint8_t byte = 0;
  clockUs = 0;
  pinValues[Config::ColorScl] = LOW;
  assert(!colorBus.read(Config::LtrAddress, 6, &byte, 1));
  assert(clockUs <= Config::SENSOR_READ_TIMEOUT_MS * 1000UL);
  std::cout << "Firmware tests passed: protocol bounds/checksums/resync, range failure/recovery, debounce, sound peak, I2C deadline.\n";
}
