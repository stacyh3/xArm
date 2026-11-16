#pragma once
#include "Arduino.h"

struct FakeWire {
  uint8_t count = 2, position = 0, transmissionError = 0;
  bool timeout = false;
  uint8_t data[2] = {0x34, 0x12};
  void clearWireTimeoutFlag() { timeout = false; }
  bool getWireTimeoutFlag() const { return timeout; }
  void beginTransmission(uint8_t) {}
  void write(uint8_t) {}
  uint8_t endTransmission() const { return transmissionError; }
  uint8_t requestFrom(uint8_t, uint8_t) { position = 0; return count; }
  int available() const { return count - position; }
  uint8_t read() { return data[position++]; }
};
extern FakeWire Wire;
