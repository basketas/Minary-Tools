#pragma once

#include <Windows.h>

unsigned char* ChangeDnsNameToTextFormat(unsigned char* reader, unsigned char* buffer, int* count);
void ChangeTextToDnsNameFormat(unsigned char* dns, unsigned char* host);
