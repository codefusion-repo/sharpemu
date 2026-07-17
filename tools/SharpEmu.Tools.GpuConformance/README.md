<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->

# NVIDIA Vulkan execution probe

This tool is a local, opt-in check for the synthetic `exec` compute fixture. It
is not part of the solution, test suite, or CI workflow. It accepts only the
`exec-cs.spv` module produced by `SharpEmu.Tools.ShaderDump`; captured shaders,
games, firmware, and other inputs are intentionally unsupported.

Generate the synthetic modules, then run the probe explicitly:

```sh
dotnet run --project tools/SharpEmu.Tools.ShaderDump/SharpEmu.Tools.ShaderDump.csproj \
  -c Release -- artifacts/shader-dump
dotnet run --project tools/SharpEmu.Tools.GpuConformance/SharpEmu.Tools.GpuConformance.csproj \
  -c Release -- artifacts/shader-dump/exec-cs.spv
```

The probe selects only an NVIDIA Vulkan physical device, preferring the GTX
1070 device ID when more than one is present. It logs the Vulkan API version,
vendor ID, device ID, driver version, and compute queue selection. A 1x1x1
dispatch writes a 64-byte host-visible storage buffer; readback checks the ALU
results, verifies that the `EXEC=0` store preserved its sentinel, and confirms
that all trailing words remain untouched.

The public command supervises Vulkan work in a child process. The GPU fence has
a 10-second timeout and the complete child process has a 15-second limit. Normal
and controlled failure paths release Vulkan resources in reverse dependency
order. If a driver call hangs beyond the process limit, the supervisor
terminates the worker so the operating system reclaims its remaining resources.

An absent Vulkan loader, missing NVIDIA device or compute queue, rejected
pipeline, timeout, or readback mismatch produces a diagnostic and a non-zero
exit code. Logs include no input paths or shader data.
