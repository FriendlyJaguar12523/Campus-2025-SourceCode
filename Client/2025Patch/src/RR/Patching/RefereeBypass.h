#pragma once

// Referee Bypass - NOP the 4 anti-tamper checks
void (*nop_O)();
void nop_H() { return; }