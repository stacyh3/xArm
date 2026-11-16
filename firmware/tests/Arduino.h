#pragma once
#include <stdint.h>
#include <stddef.h>
#include <stdio.h>
#include <deque>
#include <string>

constexpr uint8_t A0 = 14, A2 = 16;
constexpr uint8_t INPUT = 0, OUTPUT = 1, LOW = 0, HIGH = 1;
extern uint32_t clockUs;
extern int pinValues[20];
extern uint16_t adcValue;
inline uint32_t micros() { clockUs += 10; return clockUs; }
inline void delayMicroseconds(unsigned int us) { clockUs += us; }
inline void pinMode(uint8_t, uint8_t) {}
inline void digitalWrite(uint8_t, uint8_t) {}
inline int digitalRead(uint8_t pin) { return pinValues[pin]; }
inline uint16_t analogRead(uint8_t) { return adcValue; }
struct FakeSerial {
  std::deque<char> input;
  std::string output;
  int availableForWrite() const { return 64; }
  int available() const { return static_cast<int>(input.size()); }
  char read() { char value = input.front(); input.pop_front(); return value; }
  void write(const uint8_t *bytes, uint8_t length) {
    output.append(reinterpret_cast<const char *>(bytes), length);
  }
};
extern FakeSerial Serial;
