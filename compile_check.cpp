// Compile-only validation for the generated header.
//   cl /std:c++20 /EHsc /c /DWIN32_LEAN_AND_MEAN /DNOMINMAX ^
//      /I C:\UserData\projects\AlienTrainerPlus compile_check.cpp
// The header's self-check block instantiates every generated accessor, so a
// successful compile means no template instantiation errors hide behind
// unexercised members.
#define URTYPEGEN_SELF_CHECK 1
#include "mod/game/game_types.h"
