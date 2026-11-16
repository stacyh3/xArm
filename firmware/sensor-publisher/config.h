#pragma once
#include <Arduino.h>

namespace Config {
constexpr uint32_t Baud = 115200;
constexpr uint8_t ProtocolVersion = 2;
constexpr uint8_t IrPins[] = {2, 4, 10};
constexpr uint8_t TouchPin = 12;
constexpr uint8_t LightPin = A2;
constexpr uint8_t SoundPin = A0;
constexpr uint8_t ColorSda = 6;
constexpr uint8_t ColorScl = 7;
constexpr uint8_t RangeAddress = 0x77;
constexpr uint8_t LtrAddress = 0x53;
constexpr uint8_t ApdsAddress = 0x39;
constexpr uint16_t DigitalMs = 5;
constexpr uint16_t DebounceMs = 10;
constexpr uint16_t LightMs = 50;
constexpr uint16_t SoundMs = 20;
constexpr uint16_t SoundSampleMs = 1;
constexpr uint16_t RangeMs = 50;
constexpr uint16_t ColorMs = 100;
constexpr uint16_t ColorStartupMs = 150;
constexpr uint16_t RetryMs = 1000;
constexpr uint8_t DefaultHz = 20;
constexpr uint8_t MaxHz = 50;
constexpr uint16_t SENSOR_READ_TIMEOUT_MS = 5;
constexpr uint16_t ColorBusBudgetUs = SENSOR_READ_TIMEOUT_MS * 1000U - 100U;
constexpr uint16_t WireTimeoutUs = 2000;
constexpr size_t MAX_LINE_LENGTH = 128;
constexpr uint8_t RxBytesPerLoop = 16;
#ifndef SENSOR_PUBLISHER_FAKE
#define SENSOR_PUBLISHER_FAKE 0
#endif
constexpr bool Fake = SENSOR_PUBLISHER_FAKE != 0;
}
