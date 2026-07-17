// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// End-to-end pipeline tests: synthetic GFX10 program -> decode -> scalar evaluation -> SPIR-V.
// Each test asserts the expected OpAtomic* instructions land in the emitted module.
public sealed class Gen5SpirvAtomicTranslationTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;
    private const ulong BufferAddress = 0x1_0000_1000;

    [Fact]
    public void BufferAtomics_EmitAtomicOpcodes()
    {
        // BUFFER_ATOMIC_UMAX v1, BUFFER_ATOMIC_CMPSWAP v[1:2], BUFFER_ATOMIC_INC v1,
        // all against the V# in s[0:3].
        var opcodes = CompileCompute(
            [
                0xE0E04008, 0x80000100,
                0xE0C44000, 0x80000100,
                0xE0F00000, 0x80000100,
            ],
            BufferDescriptorRegisters());

        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicIIncrement, opcodes);
    }

    [Fact]
    public void DataShareAtomics_EmitAtomicOpcodes()
    {
        // DS_ADD_RTN_U32 v3, v0, v1; DS_CMPST_RTN_B32 v3, v0, v1, v2; DS_MAX_U32 v0, v1.
        var opcodes = CompileCompute(
            [
                0xD8800000, 0x03000100,
                0xD8C00000, 0x03020100,
                0xD8200000, 0x00000100,
            ],
            new Dictionary<uint, uint>());

        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicUMax, opcodes);
    }

    [Fact]
    public void ImageAtomicAdd_EmitsTexelPointerAndAtomicAdd()
    {
        // IMAGE_ATOMIC_ADD v2, v[0:1], s[4:11] dmask:0x1 dim:2D glc against an R32ui T#.
        var opcodes = CompileCompute(
            [0xF0442100, 0x00010200],
            new Dictionary<uint, uint>
            {
                // Descriptor word1 dataFormat (bits 28:20) = 20 selects R32ui/Uint.
                [5] = 20u << 20,
            });

        Assert.Contains((ushort)SpirvOp.ImageTexelPointer, opcodes);
        Assert.Contains((ushort)SpirvOp.AtomicIAdd, opcodes);
    }

    [Fact]
    public void BufferAtomicClamp_EmitsCompareExchangeRetryLoops()
    {
        // BUFFER_ATOMIC_INC/DEC v1, off, s[0:3] with distinct dword offsets.
        var spirv = CompileComputeSpirv(
            [
                0xE0F00000, 0x80000100,
                0xE0F40004, 0x80000100,
            ],
            BufferDescriptorRegisters());

        AssertAtomicClampLowering(
            spirv,
            expectedCompareExchanges: 2,
            expectedScope: 1,
            expectedEqualSemantics: 0x48,
            expectedUnequalSemantics: 0x42);
    }

    [Fact]
    public void ImageAtomicClamp_EmitsCompareExchangeRetryLoops()
    {
        // IMAGE_ATOMIC_INC/DEC v2, v[0:1], s[4:11] against an R32ui T#.
        var spirv = CompileComputeSpirv(
            [
                0xF06C2100, 0x00010200,
                0xF0702100, 0x00010200,
            ],
            new Dictionary<uint, uint>
            {
                [5] = 20u << 20,
            });

        AssertAtomicClampLowering(
            spirv,
            expectedCompareExchanges: 2,
            expectedScope: 1,
            expectedEqualSemantics: 0x808,
            expectedUnequalSemantics: 0x802);
        Assert.Contains((ushort)SpirvOp.ImageTexelPointer, CollectOpcodes(spirv));
    }

    [Fact]
    public void DataShareAtomicClamp_EmitsCompareExchangeRetryLoops()
    {
        // DS_INC_RTN_U32 v3, v0, v1; DS_DEC_RTN_U32 v4, v0, v2.
        var spirv = CompileComputeSpirv(
            [
                0xD88C0000, 0x03000100,
                0xD8900000, 0x04000200,
            ],
            new Dictionary<uint, uint>());

        AssertAtomicClampLowering(
            spirv,
            expectedCompareExchanges: 2,
            expectedScope: 2,
            expectedEqualSemantics: 0x108,
            expectedUnequalSemantics: 0x102);
    }

    private static Dictionary<uint, uint> BufferDescriptorRegisters() => new()
    {
        // V# in s[0:3]: base=BufferAddress, stride=0, numRecords=64 bytes, type=0.
        [0] = unchecked((uint)BufferAddress),
        [1] = (uint)(BufferAddress >> 32),
        [2] = 64,
        [3] = 0,
    };

    private static HashSet<ushort> CompileCompute(
        uint[] programWords,
        Dictionary<uint, uint> userDataSgprs) =>
        CollectOpcodes(CompileComputeSpirv(programWords, userDataSgprs));

    private static byte[] CompileComputeSpirv(
        uint[] programWords,
        Dictionary<uint, uint> userDataSgprs)
    {
        var memory = new FakeCpuMemory(ShaderAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Gen5ShaderAtomicDecodeTests.WriteProgram(memory, ShaderAddress, programWords);
        // COMPUTE_PGM_RSRC2 advertises 16 user SGPRs; the user data words at
        // COMPUTE_USER_DATA_0 + index seed s[0..15] for the scalar evaluator.
        var shaderRegisters = new Dictionary<uint, uint>
        {
            [Gen5ShaderAtomicDecodeTests.ComputePgmRsrc2Register] = 16u << 1,
        };
        foreach (var (sgpr, value) in userDataSgprs)
        {
            shaderRegisters[Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister + sgpr] = value;
        }

        Assert.True(
            Gen5ShaderTranslator.TryCreateState(
                ctx,
                ShaderAddress,
                0,
                shaderRegisters,
                Gen5ShaderAtomicDecodeTests.ComputeUserDataRegister,
                out var state,
                out var error),
            error);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(ctx, state, out var evaluation, out error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out var shader,
                out error),
            error);
        return shader.Spirv;
    }

    private static void AssertAtomicClampLowering(
        byte[] spirv,
        int expectedCompareExchanges,
        uint expectedScope,
        uint expectedEqualSemantics,
        uint expectedUnequalSemantics)
    {
        var instructions = ParseInstructions(spirv);
        var opcodes = instructions.Select(instruction => instruction.Opcode).ToList();
        var constants = instructions
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.Constant)
            .ToDictionary(instruction => instruction.Operands[1], instruction => instruction.Operands[2]);
        var compareExchanges = instructions
            .Where(instruction => instruction.Opcode == (ushort)SpirvOp.AtomicCompareExchange)
            .ToArray();
        Assert.Equal(
            expectedCompareExchanges,
            compareExchanges.Length);
        Assert.All(compareExchanges, instruction =>
        {
            Assert.Equal(expectedScope, constants[instruction.Operands[3]]);
            Assert.Equal(expectedEqualSemantics, constants[instruction.Operands[4]]);
            Assert.Equal(expectedUnequalSemantics, constants[instruction.Operands[5]]);
        });
        Assert.True(
            opcodes.Count(opcode => opcode == (ushort)SpirvOp.LoopMerge) >=
            expectedCompareExchanges + 1);
        Assert.Contains((ushort)SpirvOp.UGreaterThanEqual, opcodes);
        Assert.Contains((ushort)SpirvOp.UGreaterThan, opcodes);
        Assert.Contains((ushort)SpirvOp.LogicalOr, opcodes);
        Assert.Contains((ushort)SpirvOp.IAdd, opcodes);
        Assert.Contains((ushort)SpirvOp.ISub, opcodes);
        Assert.Contains((ushort)SpirvOp.Select, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicIIncrement, opcodes);
        Assert.DoesNotContain((ushort)SpirvOp.AtomicIDecrement, opcodes);
    }

    private static HashSet<ushort> CollectOpcodes(byte[] spirv)
        => CollectOpcodeSequence(spirv).ToHashSet();

    private static List<ushort> CollectOpcodeSequence(byte[] spirv)
        => ParseInstructions(spirv).Select(instruction => instruction.Opcode).ToList();

    private static List<(ushort Opcode, uint[] Operands)> ParseInstructions(byte[] spirv)
    {
        var instructions = new List<(ushort Opcode, uint[] Operands)>();
        // 5-word SPIR-V header, then (wordCount << 16 | opcode) packed instructions.
        for (var offset = 5 * sizeof(uint); offset + sizeof(uint) <= spirv.Length;)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(
                spirv.AsSpan(offset, sizeof(uint)));
            var wordCount = Math.Max((int)(word >> 16), 1);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BinaryPrimitives.ReadUInt32LittleEndian(
                    spirv.AsSpan(offset + ((index + 1) * sizeof(uint)), sizeof(uint)));
            }

            instructions.Add(((ushort)word, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }
}
