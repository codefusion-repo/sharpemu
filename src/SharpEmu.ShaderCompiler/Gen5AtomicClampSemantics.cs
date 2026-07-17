// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5AtomicClampSemantics
{
    public static uint Increment(uint oldValue, uint limit) =>
        oldValue >= limit ? 0u : oldValue + 1u;

    public static uint Decrement(uint oldValue, uint limit) =>
        oldValue == 0 || oldValue > limit ? limit : oldValue - 1u;
}
