// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5InstructionHandlingContractTests
{
    private const ulong ShaderAddress = 0x1_0000_0000;

    [Fact]
    public void IntentionalNoOps_AreExplicitAndJustified()
    {
        string[] opcodes =
        [
            "SNop",
            "SWaitcnt",
            "SWaitcntDepctr",
            "SInstPrefetch",
            "SClause",
            "STtraceData",
        ];

        foreach (var opcode in opcodes)
        {
            var instruction = Instruction(Gen5ShaderEncoding.Sopp, opcode);

            Assert.True(
                Gen5InstructionHandlingContract.TryGetIntentionalNoOpReason(
                    instruction,
                    out var reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        Assert.False(
            Gen5InstructionHandlingContract.TryGetIntentionalNoOpReason(
                Instruction(Gen5ShaderEncoding.Sopp, "SSendmsg"),
                out _));
        Assert.False(
            Gen5InstructionHandlingContract.TryGetIntentionalNoOpReason(
                Instruction(Gen5ShaderEncoding.Vintrp, "VInterpMovF32"),
                out _));
    }

    [Fact]
    public void IntentionalNoOps_TraverseDecodeEvaluateAndEmit()
    {
        var (ctx, state) = Decode(
            0xBFA10001, // s_clause 0x1
            0xBFA30000, // s_waitcnt_depctr 0x0
            0xBF810000); // s_endpgm

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.True(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out _,
                out error),
            error);
    }

    [Fact]
    public void UnsupportedDecodedInstruction_FailsAfterEvaluationWithStableIdentity()
    {
        var (ctx, state) = Decode(
            0xBF900000, // s_sendmsg 0x0
            0xBF810000); // s_endpgm

        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.False(
            Gen5SpirvTranslator.TryCompileComputeShader(
                state,
                evaluation,
                1,
                1,
                1,
                out _,
                out error));
        Assert.Equal(
            "block=0x0: emit-failed opcode=SSendmsg encoding=Sopp pc=0x0 " +
            "words=[0xBF900000] detail=unsupported decoded instruction",
            error);
    }

    [Fact]
    public void VInterpMovF32_FailsBeforeInterpolationLoweringWithStableIdentity()
    {
        var (ctx, state) = Decode(
            0xC8020000, // v_interp_mov_f32 v0, p0, attr0.x
            0xBF810000); // s_endpgm

        Assert.Equal("VInterpMovF32", state.Program.Instructions[0].Opcode);
        Assert.True(
            Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                state,
                out var evaluation,
                out var error),
            error);
        Assert.False(
            Gen5SpirvTranslator.TryCompilePixelShader(
                state,
                evaluation,
                Gen5PixelOutputKind.Float,
                out _,
                out error));
        Assert.Equal(
            "block=0x0: emit-failed opcode=VInterpMovF32 encoding=Vintrp " +
            "pc=0x0 words=[0xC8020000] detail=unsupported decoded instruction",
            error);
    }

    private static Gen5ShaderInstruction Instruction(
        Gen5ShaderEncoding encoding,
        string opcode) =>
        new(0, encoding, opcode, [0], [], [], null);

    private static (CpuContext Context, Gen5ShaderState State) Decode(
        params uint[] words)
    {
        var memory = new ProgramMemory(ShaderAddress, words);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(
            Gen5ShaderTranslator.TryDecodeProgram(
                context,
                ShaderAddress,
                out var program,
                out var error),
            error);
        return (context, new Gen5ShaderState(program, [], Metadata: null));
    }

    private sealed class ProgramMemory : ICpuMemory
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _data;

        public ProgramMemory(ulong baseAddress, IReadOnlyList<uint> words)
        {
            _baseAddress = baseAddress;
            _data = new byte[words.Count * sizeof(uint)];
            for (var index = 0; index < words.Count; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(
                    _data.AsSpan(index * sizeof(uint), sizeof(uint)),
                    words[index]);
            }
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (virtualAddress < _baseAddress)
            {
                return false;
            }

            var offset = virtualAddress - _baseAddress;
            if (offset > (ulong)_data.Length ||
                (ulong)destination.Length > (ulong)_data.Length - offset)
            {
                return false;
            }

            _data.AsSpan((int)offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source) => false;
    }
}
