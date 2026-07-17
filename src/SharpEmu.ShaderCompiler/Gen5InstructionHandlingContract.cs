// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler;

public static class Gen5InstructionHandlingContract
{
    public static bool TryGetIntentionalNoOpReason(
        Gen5ShaderInstruction instruction,
        out string reason)
    {
        reason = (instruction.Encoding, instruction.Opcode) switch
        {
            (Gen5ShaderEncoding.Sopp, "SNop") =>
                "architectural no-op",
            (Gen5ShaderEncoding.Sopp, "SWaitcnt") =>
                "lowering completes memory operations synchronously",
            (Gen5ShaderEncoding.Sopp, "SWaitcntDepctr") =>
                "lowering has no deferred instruction dependencies",
            (Gen5ShaderEncoding.Sopp, "SInstPrefetch") =>
                "instruction-cache prefetch is a hardware scheduling hint",
            (Gen5ShaderEncoding.Sopp, "SClause") =>
                "instruction clause is a hardware scheduling hint",
            (Gen5ShaderEncoding.Sopp, "STtraceData") =>
                "hardware trace payload does not affect shader results",
            _ => string.Empty,
        };
        return reason.Length != 0;
    }

    public static string FormatEmitFailure(
        Gen5ShaderInstruction instruction,
        string detail)
    {
        var words = string.Join(
            ",",
            instruction.Words.Select(word => $"0x{word:X8}"));
        return
            $"emit-failed opcode={instruction.Opcode} " +
            $"encoding={instruction.Encoding} pc=0x{instruction.Pc:X} " +
            $"words=[{words}] detail={detail}";
    }
}
