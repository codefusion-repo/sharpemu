// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5AtomicClampSemanticsTests
{
    [Theory]
    [InlineData(2u, 5u, 3u)]
    [InlineData(5u, 5u, 0u)]
    [InlineData(6u, 5u, 0u)]
    [InlineData(0u, 0u, 0u)]
    [InlineData(0u, 5u, 1u)]
    [InlineData(0xFFFFFFFEu, uint.MaxValue, uint.MaxValue)]
    [InlineData(uint.MaxValue, uint.MaxValue, 0u)]
    public void Increment_MatchesRdna2Clamp(uint oldValue, uint limit, uint expected) =>
        Assert.Equal(expected, Gen5AtomicClampSemantics.Increment(oldValue, limit));

    [Theory]
    [InlineData(3u, 5u, 2u)]
    [InlineData(5u, 5u, 4u)]
    [InlineData(6u, 5u, 5u)]
    [InlineData(0u, 5u, 5u)]
    [InlineData(0u, 0u, 0u)]
    [InlineData(1u, uint.MaxValue, 0u)]
    [InlineData(uint.MaxValue, uint.MaxValue, 0xFFFFFFFEu)]
    public void Decrement_MatchesRdna2Clamp(uint oldValue, uint limit, uint expected) =>
        Assert.Equal(expected, Gen5AtomicClampSemantics.Decrement(oldValue, limit));
}
