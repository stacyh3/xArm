#pragma once
#include "config.h"
#include <avr/pgmspace.h>
#include <stdarg.h>
#include <stdlib.h>
#include <string.h>

class Protocol {
  char tx_[Config::MAX_LINE_LENGTH + 2], rx_[Config::MAX_LINE_LENGTH + 2];
  uint8_t txLength_ = 0, txOffset_ = 0, rxLength_ = 0;
  bool discarding_ = false;
  static int8_t hex(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return -1;
  }
public:
  uint32_t rejected = 0, dropped = 0, sequence = 0;
  uint8_t hz = Config::DefaultHz;
  bool helloRequested = false;
  bool send(PGM_P format, ...) {
    if (txLength_) { ++dropped; return false; }
    va_list args;
    va_start(args, format);
    int count = vsnprintf_P(tx_, sizeof(tx_) - 4, format, args);
    va_end(args);
    if (count < 0 || count > int(Config::MAX_LINE_LENGTH - 3)) {
      ++dropped; return false;
    }
    uint8_t checksum = 0;
    for (int i = 0; i < count; ++i) checksum ^= uint8_t(tx_[i]);
    const char digits[] = "0123456789ABCDEF";
    tx_[count++] = '*';
    tx_[count++] = digits[checksum >> 4];
    tx_[count++] = digits[checksum & 15];
    tx_[count++] = '\n';
    txLength_ = count; txOffset_ = 0;
    return true;
  }
  bool busy() const { return txLength_ != 0; }
  void pump() {
    int available = Serial.availableForWrite();
    uint8_t remaining = txLength_ - txOffset_;
    uint8_t count = available < remaining ? available : remaining;
    if (count) {
      Serial.write(reinterpret_cast<const uint8_t *>(tx_ + txOffset_), count);
      txOffset_ += count;
      if (txOffset_ == txLength_) txLength_ = txOffset_ = 0;
    }
    for (uint8_t i = 0; i < Config::RxBytesPerLoop && Serial.available(); ++i) {
      char c = Serial.read();
      if (c == '\n') {
        if (discarding_) ++rejected;
        else {
          if (rxLength_ && rx_[rxLength_ - 1] == '\r') --rxLength_;
          rx_[rxLength_] = 0;
          command();
        }
        rxLength_ = 0; discarding_ = false;
      } else if (!discarding_) {
        if (rxLength_ >= Config::MAX_LINE_LENGTH || c < 32 || c > 126) {
          if (c == '\r' && rxLength_ <= Config::MAX_LINE_LENGTH) rx_[rxLength_++] = c;
          else discarding_ = true;
        } else rx_[rxLength_++] = c;
      }
    }
  }
private:
  void command() {
    char *star = strchr(rx_, '*');
    if (!star || star - rx_ != rxLength_ - 3 || hex(star[1]) < 0 || hex(star[2]) < 0) {
      ++rejected; return;
    }
    uint8_t sum = 0;
    for (char *p = rx_; p != star; ++p) sum ^= uint8_t(*p);
    if (sum != ((hex(star[1]) << 4) | hex(star[2]))) { ++rejected; return; }
    *star = 0;
    if (!strcmp_P(rx_, PSTR("P"))) { helloRequested = true; return; }
    if (!strncmp_P(rx_, PSTR("C,hz,"), 5)) {
      char *value = rx_ + 5;
      if (!*value) { ++rejected; return; }
      uint16_t rate = 0;
      for (; *value; ++value) {
        if (*value < '0' || *value > '9' || rate > Config::MaxHz) { ++rejected; return; }
        rate = rate * 10 + (*value - '0');
      }
      if (rate >= 1 && rate <= Config::MaxHz) { hz = rate; helloRequested = true; return; }
    }
    // D is optional and unsupported; count it rather than implying OLED success.
    ++rejected;
  }
};
