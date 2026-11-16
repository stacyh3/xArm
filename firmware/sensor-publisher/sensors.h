#pragma once
#include "config.h"
#include <Wire.h>

inline bool due(uint32_t now, uint32_t &last, uint16_t period) {
  if (uint32_t(now - last) < period) return false;
  last = now;
  return true;
}

class DigitalSensor {
  uint8_t pin_;
  bool value_ = false, candidate_ = false;
  uint32_t sampled_ = 0, changed_ = 0;
public:
  explicit DigitalSensor(uint8_t pin) : pin_(pin) {}
  void begin() {
    pinMode(pin_, INPUT);
    value_ = candidate_ = digitalRead(pin_) == LOW;
  }
  bool poll(uint32_t now) {
    if (!due(now, sampled_, Config::DigitalMs)) return false;
    bool next = digitalRead(pin_) == LOW;
    if (next != candidate_) { candidate_ = next; changed_ = now; }
    if (candidate_ != value_ && uint32_t(now - changed_) >= Config::DebounceMs) {
      value_ = candidate_;
      return true;
    }
    return false;
  }
  bool latest() const { return value_; }
  bool failed() const { return false; }
};

class AnalogSensor {
  uint8_t pin_;
  uint16_t period_, value_ = 0;
  uint32_t sampled_ = 0;
public:
  AnalogSensor(uint8_t pin, uint16_t period) : pin_(pin), period_(period) {}
  void begin() { pinMode(pin_, INPUT); }
  void poll(uint32_t now) {
    if (due(now, sampled_, period_)) value_ = analogRead(pin_);
  }
  uint16_t latest() const { return value_; }
  bool failed() const { return false; }
};

class SoundSensor {
  uint16_t peak_ = 0, value_ = 0;
  uint32_t sampled_ = 0, published_ = 0;
public:
  void begin() { pinMode(Config::SoundPin, INPUT); }
  void poll(uint32_t now) {
    if (due(now, sampled_, Config::SoundSampleMs)) {
      uint16_t sample = analogRead(Config::SoundPin);
      if (sample > peak_) peak_ = sample;
    }
    if (due(now, published_, Config::SoundMs)) { value_ = peak_; peak_ = 0; }
  }
  uint16_t latest() const { return value_; }
  bool failed() const { return false; }
};

class RangeSensor {
  uint16_t value_ = 0;
  bool failed_ = true;
  uint32_t sampled_ = 0;
public:
  void begin() {}
  void poll(uint32_t now) {
    if (!due(now, sampled_, Config::RangeMs)) return;
    uint32_t started = micros();
    Wire.clearWireTimeoutFlag();
    Wire.beginTransmission(Config::RangeAddress);
    Wire.write(uint8_t(0));
    bool ok = Wire.endTransmission() == 0;
    uint8_t bytes = ok ? Wire.requestFrom(Config::RangeAddress, uint8_t(2)) : 0;
    ok = ok && bytes == 2 && !Wire.getWireTimeoutFlag()
      && uint32_t(micros() - started) < Config::SENSOR_READ_TIMEOUT_MS * 1000UL;
    if (ok) {
      uint8_t low = Wire.read();
      value_ = uint16_t(low) | (uint16_t(Wire.read()) << 8);
    } else {
      value_ = 0;
      while (Wire.available()) Wire.read();
    }
    failed_ = !ok;
  }
  uint16_t latest() const { return value_; }
  bool failed() const { return failed_; }
};

// One deadline covers the entire software-I2C transaction, including clock stretch.
class ColorBus {
  uint32_t started_ = 0;
  bool expired() const {
    return uint32_t(micros() - started_) >= Config::ColorBusBudgetUs;
  }
  static void low(uint8_t pin) { digitalWrite(pin, LOW); pinMode(pin, OUTPUT); }
  static void release(uint8_t pin) { pinMode(pin, INPUT); }
  bool clockHigh() {
    release(Config::ColorScl);
    while (digitalRead(Config::ColorScl) == LOW) if (expired()) return false;
    delayMicroseconds(3);
    return !expired();
  }
  bool start() {
    release(Config::ColorSda);
    if (!clockHigh()) return false;
    if (digitalRead(Config::ColorSda) == LOW) return false;
    low(Config::ColorSda);
    delayMicroseconds(3);
    low(Config::ColorScl);
    return true;
  }
  void stop() {
    low(Config::ColorSda);
    release(Config::ColorScl);
    delayMicroseconds(3);
    release(Config::ColorSda);
  }
  bool writeByte(uint8_t value) {
    for (uint8_t bit = 0; bit < 8; ++bit) {
      if (value & 0x80) release(Config::ColorSda); else low(Config::ColorSda);
      delayMicroseconds(3);
      if (!clockHigh()) return false;
      low(Config::ColorScl);
      value <<= 1;
    }
    release(Config::ColorSda);
    if (!clockHigh()) return false;
    bool ack = digitalRead(Config::ColorSda) == LOW;
    low(Config::ColorScl);
    return ack;
  }
  bool readByte(uint8_t &value, bool ack) {
    value = 0;
    release(Config::ColorSda);
    for (uint8_t bit = 0; bit < 8; ++bit) {
      if (!clockHigh()) return false;
      value = (value << 1) | (digitalRead(Config::ColorSda) != LOW);
      low(Config::ColorScl);
      delayMicroseconds(3);
    }
    if (ack) low(Config::ColorSda); else release(Config::ColorSda);
    if (!clockHigh()) return false;
    low(Config::ColorScl);
    release(Config::ColorSda);
    return true;
  }
public:
  void begin() { release(Config::ColorSda); release(Config::ColorScl); }
  bool read(uint8_t address, uint8_t reg, uint8_t *data, uint8_t count) {
    started_ = micros();
    bool ok = start() && writeByte(address << 1) && writeByte(reg)
      && start() && writeByte((address << 1) | 1);
    for (uint8_t i = 0; ok && i < count; ++i) ok = readByte(data[i], i + 1 < count);
    stop();
    return ok && !expired();
  }
  bool write(uint8_t address, uint8_t reg, uint8_t value) {
    started_ = micros();
    bool ok = start() && writeByte(address << 1) && writeByte(reg) && writeByte(value);
    stop();
    return ok && !expired();
  }
};

struct ColorValue { uint16_t r = 0, g = 0, b = 0, clear = 0; };

class ColorSensor {
  ColorBus bus_;
  ColorValue value_;
  uint8_t model_ = 0, step_ = 0;
  bool failed_ = true;
  uint32_t sampled_ = 0, started_ = 0, fresh_ = 0;
  static uint16_t word(const uint8_t *bytes) {
    return uint16_t(bytes[0]) | (uint16_t(bytes[1]) << 8);
  }
  void fail(uint32_t now) {
    value_ = ColorValue();
    failed_ = true;
    model_ = step_ = 0;
    sampled_ = now;
  }
public:
  void begin() { bus_.begin(); }
  void poll(uint32_t now) {
    uint8_t bytes[9];
    if (!model_) {
      if (!due(now, sampled_, Config::RetryMs)) return;
      // Probe one model per poll so two failed probes cannot double the deadline.
      if (step_ == 0) {
        if (bus_.read(Config::LtrAddress, 0x06, bytes, 1) && bytes[0] == 0xC2) {
          model_ = 1; step_ = 0;
        } else { step_ = 1; sampled_ = now - Config::RetryMs; }
      } else {
        if (bus_.read(Config::ApdsAddress, 0x92, bytes, 1) && bytes[0] == 0xAB) {
          model_ = 2; step_ = 0;
        } else { step_ = 0; }
      }
      started_ = now;
      return;
    }
    uint8_t address = model_ == 1 ? Config::LtrAddress : Config::ApdsAddress;
    if (step_ < 3) {
      bool ok;
      if (model_ == 1) {
        const uint8_t registers[] = {0x00, 0x04, 0x05};
        const uint8_t values[] = {0x06, 0x42, 0x03};
        ok = bus_.write(address, registers[step_], values[step_]);
      } else {
        // APDS: 10 ms integration (ATIME 252), 4x gain, power + ALS enabled.
        const uint8_t registers[] = {0x81, 0x8F, 0x80};
        const uint8_t values[] = {252, 1, 3};
        ok = bus_.write(address, registers[step_], values[step_]);
      }
      if (!ok) { fail(now); return; }
      ++step_; started_ = now;
      return;
    }
    if (uint32_t(now - started_) < Config::ColorStartupMs) return;
    if (!due(now, sampled_, Config::ColorMs)) return;
    bool ok;
    if (model_ == 1) {
      ok = bus_.read(address, 0x0D, bytes, 9);
      if (ok) {
        value_.g = word(bytes); value_.r = word(bytes + 3);
        value_.b = word(bytes + 6); value_.clear = 0;
      }
    } else {
      // Status and data are separate bounded polls; never wait for conversion.
      if (step_ == 3) {
        ok = bus_.read(address, 0x93, bytes, 1);
        if (!ok) { fail(now); return; }
        if (!(bytes[0] & 1)) {
          if (uint32_t(now - fresh_) > Config::RetryMs) fail(now);
          return;
        }
        step_ = 4;
        sampled_ = now - Config::ColorMs;
        return;
      }
      ok = bus_.read(address, 0x94, bytes, 8);
      if (ok) {
        value_.clear = word(bytes); value_.r = word(bytes + 2);
        value_.g = word(bytes + 4); value_.b = word(bytes + 6);
      }
      step_ = 3;
    }
    if (!ok) { fail(now); return; }
    failed_ = false; fresh_ = now;
  }
  ColorValue latest() const { return value_; }
  bool failed() const { return failed_; }
  uint8_t model() const { return model_; }
};
