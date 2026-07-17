<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# NVIDIA Vulkan execution probe

This tool is a local, opt-in check for the synthetic `exec` and `atomic-clamp`
compute fixtures. It is not part of the solution, test suite, or CI workflow.
It accepts only the corresponding `*-cs.spv` modules produced by
`SharpEmu.Tools.ShaderDump`; captured shaders, games, firmware, and other inputs
are intentionally unsupported.

Generate the synthetic modules, then run the probe explicitly:

```sh
dotnet run --project tools/SharpEmu.Tools.ShaderDump/SharpEmu.Tools.ShaderDump.csproj \
  -c Release -- artifacts/shader-dump
dotnet run --project tools/SharpEmu.Tools.GpuConformance/SharpEmu.Tools.GpuConformance.csproj \
  -c Release -- artifacts/shader-dump/exec-cs.spv
dotnet run --project tools/SharpEmu.Tools.GpuConformance/SharpEmu.Tools.GpuConformance.csproj \
  -c Release -- artifacts/shader-dump/atomic-clamp-cs.spv
```

The probe selects only an NVIDIA Vulkan physical device, preferring the GTX
1070 device ID when more than one is present. It logs the Vulkan API version,
vendor ID, device ID, driver version, and compute queue selection. A 1x1x1
dispatch writes a 64-byte host-visible storage buffer. The `exec` readback
checks ALU results and EXEC masking. The `atomic-clamp` readback checks
non-maximum INC/DEC limits, previous-value returns, and a masked atomic. Both
fixtures also confirm that unused words remain untouched.

The public command supervises Vulkan work in a child process. The GPU fence has
a 10-second timeout and the complete child process has a 15-second limit. Normal
and controlled failure paths release Vulkan resources in reverse dependency
order. If a driver call hangs beyond the process limit, the supervisor
terminates the worker so the operating system reclaims its remaining resources.

An absent Vulkan loader, missing NVIDIA device or compute queue, rejected
pipeline, timeout, or readback mismatch produces a diagnostic and a non-zero
exit code. Logs include no input paths or shader data.
