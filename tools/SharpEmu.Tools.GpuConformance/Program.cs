// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VulkanBuffer = Silk.NET.Vulkan.Buffer;

return await NvidiaVulkanProbe.RunAsync(args);

internal static class NvidiaVulkanProbe
{
    private const uint NvidiaVendorId = 0x10DE;
    private const uint Gtx1070DeviceId = 0x1B81;
    private const uint Sentinel = 0xCAFEBABE;
    private const ulong BufferSize = 64;
    private const ulong FenceTimeoutNanoseconds = 10_000_000_000;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 2 && args[0] == "--worker")
        {
            return RunWorker(args[1]);
        }

        if (args.Length != 1 || args[0] is "-h" or "--help")
        {
            PrintUsage();
            return args.Length == 1 ? 0 : 2;
        }

        if (!TryGetFixture(args[0], out _))
        {
            Console.Error.WriteLine(
                "ERROR: expected ShaderDump's synthetic exec-cs.spv or atomic-clamp-cs.spv fixture; other shader inputs are not accepted");
            return 2;
        }

        return await RunSupervisedWorkerAsync(args[0]);
    }

    private static async Task<int> RunSupervisedWorkerAsync(string shaderPath)
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            Console.Error.WriteLine("ERROR: unable to locate the probe executable for supervised execution");
            return 1;
        }

        using var worker = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = processPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
        };
        worker.StartInfo.ArgumentList.Add("--worker");
        worker.StartInfo.ArgumentList.Add(shaderPath);

        try
        {
            if (!worker.Start())
            {
                Console.Error.WriteLine("ERROR: failed to start the supervised Vulkan worker");
                return 1;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine(
                $"ERROR: failed to start the supervised Vulkan worker ({exception.GetType().Name})");
            return 1;
        }

        var stdoutTask = worker.StandardOutput.ReadToEndAsync();
        var stderrTask = worker.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(ProcessTimeout);

        try
        {
            await worker.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync();
            }
            catch (InvalidOperationException)
            {
                // The process exited between the timeout and the kill request.
            }

            WriteWorkerOutput(await stdoutTask, await stderrTask);
            Console.Error.WriteLine(
                "ERROR: Vulkan probe exceeded the 15 second limit; worker terminated and process resources were reclaimed");
            return 124;
        }

        WriteWorkerOutput(await stdoutTask, await stderrTask);
        return worker.ExitCode;
    }

    private static int RunWorker(string shaderPath)
    {
        try
        {
            var (code, fixture) = ReadFixture(shaderPath);
            return RunVulkan(code, fixture);
        }
        catch (ProbeFailureException exception)
        {
            Console.Error.WriteLine($"ERROR: {exception.Message}");
            return 1;
        }
        catch (DllNotFoundException)
        {
            Console.Error.WriteLine(
                "ERROR: Vulkan loader unavailable; install a Vulkan-capable NVIDIA driver and loader");
            return 1;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"ERROR: unexpected Vulkan probe failure ({exception.GetType().Name}); rerun with a current NVIDIA driver");
            return 1;
        }
    }

    private static (byte[] Code, ProbeFixture Fixture) ReadFixture(string shaderPath)
    {
        if (!TryGetFixture(shaderPath, out var fixture))
        {
            throw new ProbeFailureException(
                "expected ShaderDump's synthetic exec-cs.spv or atomic-clamp-cs.spv fixture; other shader inputs are not accepted");
        }

        var fixtureName = Path.GetFileName(shaderPath);

        byte[] code;
        try
        {
            code = File.ReadAllBytes(shaderPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProbeFailureException(
                $"could not read the synthetic {fixtureName} fixture ({exception.GetType().Name})");
        }

        if (code.Length < sizeof(uint) || code.Length % sizeof(uint) != 0 ||
            BitConverter.ToUInt32(code) != 0x07230203)
        {
            throw new ProbeFailureException(
                $"synthetic {fixtureName} is not a complete SPIR-V module; regenerate it with ShaderDump");
        }

        Console.WriteLine($"fixture: synthetic {fixtureName} ({code.Length} bytes)");
        return (code, fixture);
    }

    private static unsafe int RunVulkan(byte[] code, ProbeFixture fixture)
    {
        Vk vk;
        try
        {
            vk = Vk.GetApi();
        }
        catch (Exception exception) when (exception is DllNotFoundException or TypeInitializationException)
        {
            throw new ProbeFailureException(
                "Vulkan loader unavailable; install a Vulkan-capable NVIDIA driver and loader");
        }

        using (vk)
        {
            Instance instance = default;
            Device device = default;
            VulkanBuffer buffer = default;
            DeviceMemory memory = default;
            ShaderModule module = default;
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            Pipeline pipeline = default;
            DescriptorPool descriptorPool = default;
            CommandPool commandPool = default;
            Fence fence = default;
            void* mapped = null;
            nint appName = 0;
            nint entryName = 0;
            var queueSubmitted = false;
            var queueCompleted = false;

            try
            {
                appName = SilkMarshal.StringToPtr("SharpEmuNvidiaVulkanProbe");
                var appInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = (byte*)appName,
                    ApiVersion = Vk.Version12,
                };
                var instanceInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    PApplicationInfo = &appInfo,
                };
                Check(vk.CreateInstance(in instanceInfo, null, out instance), "vkCreateInstance");

                var (physical, properties) = SelectNvidiaDevice(vk, instance);
                var deviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";
                Console.WriteLine(
                    $"device: name={deviceName}; api={FormatApiVersion(properties.ApiVersion)}; " +
                    $"vendor=0x{properties.VendorID:X4}; device=0x{properties.DeviceID:X4}; " +
                    $"driver={FormatDriverVersion(properties.DriverVersion)} (0x{properties.DriverVersion:X8})");

                var (computeFamily, queueFlags) = SelectComputeQueue(vk, physical);
                Console.WriteLine(
                    $"queue: family={computeFamily}; index=0; flags={queueFlags}");

                vk.GetPhysicalDeviceFeatures(physical, out var supportedFeatures);
                if (!supportedFeatures.ShaderInt64)
                {
                    throw new ProbeFailureException(
                        "selected NVIDIA device lacks shaderInt64 required by the emitted SPIR-V");
                }

                var priority = 1f;
                var queueInfo = new DeviceQueueCreateInfo
                {
                    SType = StructureType.DeviceQueueCreateInfo,
                    QueueFamilyIndex = computeFamily,
                    QueueCount = 1,
                    PQueuePriorities = &priority,
                };
                var features = new PhysicalDeviceFeatures { ShaderInt64 = true };
                var deviceInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    PEnabledFeatures = &features,
                };
                Check(vk.CreateDevice(physical, in deviceInfo, null, out device), "vkCreateDevice");
                vk.GetDeviceQueue(device, computeFamily, 0, out var queue);

                var bufferInfo = new BufferCreateInfo
                {
                    SType = StructureType.BufferCreateInfo,
                    Size = BufferSize,
                    Usage = BufferUsageFlags.StorageBufferBit,
                    SharingMode = SharingMode.Exclusive,
                };
                Check(vk.CreateBuffer(device, in bufferInfo, null, out buffer), "vkCreateBuffer");
                vk.GetBufferMemoryRequirements(device, buffer, out var requirements);
                vk.GetPhysicalDeviceMemoryProperties(physical, out var memoryProperties);

                var memoryType = FindReadbackMemoryType(requirements, memoryProperties);
                var allocateInfo = new MemoryAllocateInfo
                {
                    SType = StructureType.MemoryAllocateInfo,
                    AllocationSize = requirements.Size,
                    MemoryTypeIndex = memoryType,
                };
                Check(vk.AllocateMemory(device, in allocateInfo, null, out memory), "vkAllocateMemory");
                Check(vk.BindBufferMemory(device, buffer, memory, 0), "vkBindBufferMemory");
                Check(vk.MapMemory(device, memory, 0, BufferSize, 0, &mapped), "vkMapMemory");

                var words = (uint*)mapped;
                for (var index = 0; index < (int)(BufferSize / sizeof(uint)); index++)
                {
                    words[index] = Sentinel;
                }
                InitializeFixture(words, fixture);

                fixed (byte* pCode = code)
                {
                    var moduleInfo = new ShaderModuleCreateInfo
                    {
                        SType = StructureType.ShaderModuleCreateInfo,
                        CodeSize = (nuint)code.Length,
                        PCode = (uint*)pCode,
                    };
                    Check(
                        vk.CreateShaderModule(device, in moduleInfo, null, out module),
                        "vkCreateShaderModule");
                }

                var layoutBinding = new DescriptorSetLayoutBinding
                {
                    Binding = 0,
                    DescriptorType = DescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                    StageFlags = ShaderStageFlags.ComputeBit,
                };
                var setLayoutInfo = new DescriptorSetLayoutCreateInfo
                {
                    SType = StructureType.DescriptorSetLayoutCreateInfo,
                    BindingCount = 1,
                    PBindings = &layoutBinding,
                };
                Check(
                    vk.CreateDescriptorSetLayout(device, in setLayoutInfo, null, out setLayout),
                    "vkCreateDescriptorSetLayout");

                var pipelineLayoutInfo = new PipelineLayoutCreateInfo
                {
                    SType = StructureType.PipelineLayoutCreateInfo,
                    SetLayoutCount = 1,
                    PSetLayouts = &setLayout,
                };
                Check(
                    vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out pipelineLayout),
                    "vkCreatePipelineLayout");

                entryName = SilkMarshal.StringToPtr("main");
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Stage = new PipelineShaderStageCreateInfo
                    {
                        SType = StructureType.PipelineShaderStageCreateInfo,
                        Stage = ShaderStageFlags.ComputeBit,
                        Module = module,
                        PName = (byte*)entryName,
                    },
                    Layout = pipelineLayout,
                };
                Check(
                    vk.CreateComputePipelines(
                        device,
                        default,
                        1,
                        in pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines");
                Console.WriteLine("pipeline: NVIDIA driver accepted the synthetic SPIR-V");

                var poolSize = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = 1,
                };
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = 1,
                    PPoolSizes = &poolSize,
                };
                Check(
                    vk.CreateDescriptorPool(device, in poolInfo, null, out descriptorPool),
                    "vkCreateDescriptorPool");

                var setAllocateInfo = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = descriptorPool,
                    DescriptorSetCount = 1,
                    PSetLayouts = &setLayout,
                };
                Check(
                    vk.AllocateDescriptorSets(device, in setAllocateInfo, out var descriptorSet),
                    "vkAllocateDescriptorSets");

                var descriptorBuffer = new DescriptorBufferInfo
                {
                    Buffer = buffer,
                    Offset = 0,
                    Range = BufferSize,
                };
                var write = new WriteDescriptorSet
                {
                    SType = StructureType.WriteDescriptorSet,
                    DstSet = descriptorSet,
                    DstBinding = 0,
                    DescriptorCount = 1,
                    DescriptorType = DescriptorType.StorageBuffer,
                    PBufferInfo = &descriptorBuffer,
                };
                vk.UpdateDescriptorSets(device, 1, in write, 0, null);

                var commandPoolInfo = new CommandPoolCreateInfo
                {
                    SType = StructureType.CommandPoolCreateInfo,
                    QueueFamilyIndex = computeFamily,
                };
                Check(
                    vk.CreateCommandPool(device, in commandPoolInfo, null, out commandPool),
                    "vkCreateCommandPool");

                var commandBufferInfo = new CommandBufferAllocateInfo
                {
                    SType = StructureType.CommandBufferAllocateInfo,
                    CommandPool = commandPool,
                    Level = CommandBufferLevel.Primary,
                    CommandBufferCount = 1,
                };
                Check(
                    vk.AllocateCommandBuffers(device, in commandBufferInfo, out var commandBuffer),
                    "vkAllocateCommandBuffers");

                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                };
                Check(vk.BeginCommandBuffer(commandBuffer, in beginInfo), "vkBeginCommandBuffer");
                vk.CmdBindPipeline(commandBuffer, PipelineBindPoint.Compute, pipeline);
                vk.CmdBindDescriptorSets(
                    commandBuffer,
                    PipelineBindPoint.Compute,
                    pipelineLayout,
                    0,
                    1,
                    in descriptorSet,
                    0,
                    null);
                vk.CmdDispatch(commandBuffer, 1, 1, 1);

                var barrier = new MemoryBarrier
                {
                    SType = StructureType.MemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.HostReadBit,
                };
                vk.CmdPipelineBarrier(
                    commandBuffer,
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.HostBit,
                    0,
                    1,
                    in barrier,
                    0,
                    null,
                    0,
                    null);
                Check(vk.EndCommandBuffer(commandBuffer), "vkEndCommandBuffer");

                var fenceInfo = new FenceCreateInfo
                {
                    SType = StructureType.FenceCreateInfo,
                };
                Check(vk.CreateFence(device, in fenceInfo, null, out fence), "vkCreateFence");

                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(vk.QueueSubmit(queue, 1, in submitInfo, fence), "vkQueueSubmit");
                queueSubmitted = true;

                var waitResult = vk.WaitForFences(
                    device,
                    1,
                    in fence,
                    true,
                    FenceTimeoutNanoseconds);
                if (waitResult == Result.Timeout)
                {
                    throw new ProbeFailureException(
                        "compute dispatch exceeded the 10 second GPU fence timeout");
                }

                Check(waitResult, "vkWaitForFences");
                queueCompleted = true;
                Console.WriteLine("dispatch: completed within 10 second GPU timeout");

                return VerifyReadback(words, fixture);
            }
            finally
            {
                if (device.Handle != 0 && queueSubmitted && !queueCompleted)
                {
                    _ = vk.DeviceWaitIdle(device);
                }

                if (device.Handle != 0 && fence.Handle != 0)
                {
                    vk.DestroyFence(device, fence, null);
                }

                if (device.Handle != 0 && commandPool.Handle != 0)
                {
                    vk.DestroyCommandPool(device, commandPool, null);
                }

                if (device.Handle != 0 && descriptorPool.Handle != 0)
                {
                    vk.DestroyDescriptorPool(device, descriptorPool, null);
                }

                if (device.Handle != 0 && pipeline.Handle != 0)
                {
                    vk.DestroyPipeline(device, pipeline, null);
                }

                if (device.Handle != 0 && pipelineLayout.Handle != 0)
                {
                    vk.DestroyPipelineLayout(device, pipelineLayout, null);
                }

                if (device.Handle != 0 && setLayout.Handle != 0)
                {
                    vk.DestroyDescriptorSetLayout(device, setLayout, null);
                }

                if (device.Handle != 0 && module.Handle != 0)
                {
                    vk.DestroyShaderModule(device, module, null);
                }

                if (device.Handle != 0 && mapped != null)
                {
                    vk.UnmapMemory(device, memory);
                }

                if (device.Handle != 0 && buffer.Handle != 0)
                {
                    vk.DestroyBuffer(device, buffer, null);
                }

                if (device.Handle != 0 && memory.Handle != 0)
                {
                    vk.FreeMemory(device, memory, null);
                }

                if (device.Handle != 0)
                {
                    vk.DestroyDevice(device, null);
                }

                if (instance.Handle != 0)
                {
                    vk.DestroyInstance(instance, null);
                }

                if (entryName != 0)
                {
                    SilkMarshal.Free(entryName);
                }

                if (appName != 0)
                {
                    SilkMarshal.Free(appName);
                }

                Console.WriteLine("cleanup: complete");
            }
        }
    }

    private static unsafe (PhysicalDevice Device, PhysicalDeviceProperties Properties) SelectNvidiaDevice(
        Vk vk,
        Instance instance)
    {
        uint deviceCount = 0;
        Check(
            vk.EnumeratePhysicalDevices(instance, &deviceCount, null),
            "vkEnumeratePhysicalDevices(count)");
        if (deviceCount == 0)
        {
            throw new ProbeFailureException(
                "no Vulkan devices found; verify the NVIDIA Vulkan driver installation");
        }

        var physicalDevices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = physicalDevices)
        {
            Check(
                vk.EnumeratePhysicalDevices(instance, &deviceCount, pDevices),
                "vkEnumeratePhysicalDevices(list)");
        }

        PhysicalDevice selected = default;
        PhysicalDeviceProperties selectedProperties = default;
        foreach (var candidate in physicalDevices)
        {
            vk.GetPhysicalDeviceProperties(candidate, out var properties);
            if (properties.VendorID != NvidiaVendorId)
            {
                continue;
            }

            selected = candidate;
            selectedProperties = properties;
            if (properties.DeviceID == Gtx1070DeviceId)
            {
                break;
            }
        }

        if (selected.Handle == 0)
        {
            throw new ProbeFailureException(
                "no NVIDIA Vulkan device found (vendor 0x10DE); software and non-NVIDIA devices are intentionally rejected");
        }

        return (selected, selectedProperties);
    }

    private static unsafe (uint Family, QueueFlags Flags) SelectComputeQueue(
        Vk vk,
        PhysicalDevice physical)
    {
        uint familyCount = 0;
        vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, null);
        if (familyCount == 0)
        {
            throw new ProbeFailureException("selected NVIDIA device reports no queue families");
        }

        var families = new QueueFamilyProperties[familyCount];
        fixed (QueueFamilyProperties* pFamilies = families)
        {
            vk.GetPhysicalDeviceQueueFamilyProperties(physical, &familyCount, pFamilies);
        }

        uint? fallback = null;
        for (uint index = 0; index < familyCount; index++)
        {
            var family = families[index];
            if (family.QueueCount == 0 || !family.QueueFlags.HasFlag(QueueFlags.ComputeBit))
            {
                continue;
            }

            fallback ??= index;
            if (!family.QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                return (index, family.QueueFlags);
            }
        }

        if (fallback is uint computeFamily)
        {
            return (computeFamily, families[computeFamily].QueueFlags);
        }

        throw new ProbeFailureException(
            "selected NVIDIA device has no compute-capable queue family");
    }

    private static uint FindReadbackMemoryType(
        MemoryRequirements requirements,
        PhysicalDeviceMemoryProperties memoryProperties)
    {
        for (var index = 0; index < memoryProperties.MemoryTypeCount; index++)
        {
            var flags = memoryProperties.MemoryTypes[index].PropertyFlags;
            if ((requirements.MemoryTypeBits & (1u << index)) != 0 &&
                flags.HasFlag(MemoryPropertyFlags.HostVisibleBit) &&
                flags.HasFlag(MemoryPropertyFlags.HostCoherentBit))
            {
                return (uint)index;
            }
        }

        throw new ProbeFailureException(
            "selected NVIDIA device has no host-visible, host-coherent memory for readback");
    }

    private static unsafe void InitializeFixture(uint* words, ProbeFixture fixture)
    {
        if (fixture != ProbeFixture.AtomicClamp)
        {
            return;
        }

        words[0] = 2;
        words[1] = 5;
        words[2] = 6;
        words[3] = 0;
        words[4] = 3;
        words[5] = 6;
        words[6] = 4;
    }

    private static unsafe int VerifyReadback(uint* words, ProbeFixture fixture) =>
        fixture switch
        {
            ProbeFixture.Exec => VerifyExecReadback(words),
            ProbeFixture.AtomicClamp => VerifyAtomicClampReadback(words),
            _ => throw new ProbeFailureException("unknown synthetic fixture"),
        };

    private static unsafe int VerifyExecReadback(uint* words)
    {
        var expectedFma = BitConverter.SingleToUInt32Bits(
            MathF.FusedMultiplyAdd(1.5f, 2.25f, 10.0f));
        var product = (long)0x7FFFFFFF * 0x00010003;
        var expectedHi = (uint)(product >> 32);
        var expectedLo = (uint)product;
        var expectedRestored = BitConverter.SingleToUInt32Bits(1.5f);

        var results = new (string Name, uint Actual, uint Expected)[]
        {
            ("v_fmac_f32", words[0], expectedFma),
            ("v_mul_hi_i32", words[1], expectedHi),
            ("v_mul_lo_i32", words[2], expectedLo),
            ("exec=0 store suppressed", words[3], Sentinel),
            ("store after exec restore", words[4], expectedRestored),
        };

        var failures = 0;
        foreach (var (name, actual, expected) in results)
        {
            var passed = actual == expected;
            failures += passed ? 0 : 1;
            Console.WriteLine(
                $"readback: {(passed ? "PASS" : "FAIL")} {name}; actual=0x{actual:X8}; expected=0x{expected:X8}");
        }

        var totalWords = (int)(BufferSize / sizeof(uint));
        for (var index = results.Length; index < totalWords; index++)
        {
            if (words[index] == Sentinel)
            {
                continue;
            }

            failures++;
            Console.WriteLine(
                $"readback: FAIL trailing word {index}; actual=0x{words[index]:X8}; expected=0x{Sentinel:X8}");
        }

        if (failures == 0)
        {
            Console.WriteLine(
                $"readback: PASS trailing words {results.Length}..{totalWords - 1} preserved sentinel");
            Console.WriteLine("RESULT: NVIDIA Vulkan exec fixture passed");
            return 0;
        }

        Console.WriteLine($"RESULT: FAIL with {failures} readback mismatch(es)");
        return 1;
    }

    private static unsafe int VerifyAtomicClampReadback(uint* words)
    {
        var expected = new (string Name, int Index, uint Value)[]
        {
            ("INC old<DATA final", 0, 3),
            ("INC old==DATA final", 1, 0),
            ("INC old>DATA final", 2, 0),
            ("DEC old==0 final", 3, 5),
            ("DEC old<DATA final", 4, 2),
            ("DEC old>DATA final", 5, 5),
            ("EXEC=0 atomic suppressed", 6, 4),
            ("unused dword 7", 7, Sentinel),
            ("INC old<DATA return", 8, 2),
            ("INC old==DATA return", 9, 5),
            ("INC old>DATA return", 10, 6),
            ("DEC old==0 return", 11, 0),
            ("DEC old<DATA return", 12, 3),
            ("DEC old>DATA return", 13, 6),
            ("unused dword 14", 14, Sentinel),
            ("unused dword 15", 15, Sentinel),
        };

        var failures = 0;
        foreach (var (name, index, value) in expected)
        {
            var actual = words[index];
            var passed = actual == value;
            failures += passed ? 0 : 1;
            Console.WriteLine(
                $"readback: {(passed ? "PASS" : "FAIL")} {name}; actual=0x{actual:X8}; expected=0x{value:X8}");
        }

        if (failures == 0)
        {
            Console.WriteLine("RESULT: NVIDIA Vulkan atomic-clamp fixture passed");
            return 0;
        }

        Console.WriteLine($"RESULT: FAIL with {failures} atomic-clamp readback mismatch(es)");
        return 1;
    }

    private static void Check(Result result, string operation)
    {
        if (result != Result.Success)
        {
            throw new ProbeFailureException($"{operation} failed ({result})");
        }
    }

    private static bool TryGetFixture(string path, out ProbeFixture fixture)
    {
        fixture = Path.GetFileName(path).ToLowerInvariant() switch
        {
            "exec-cs.spv" => ProbeFixture.Exec,
            "atomic-clamp-cs.spv" => ProbeFixture.AtomicClamp,
            _ => ProbeFixture.Unknown,
        };
        return fixture != ProbeFixture.Unknown;
    }

    private static string FormatApiVersion(uint version) =>
        $"{(version >> 22) & 0x7F}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    private static string FormatDriverVersion(uint version) =>
        $"{version >> 22}.{(version >> 14) & 0xFF}.{(version >> 6) & 0xFF}.{version & 0x3F}";

    private static void PrintUsage()
    {
        Console.WriteLine(
            "usage: SharpEmu.Tools.GpuConformance <ShaderDump-output/{exec,atomic-clamp}-cs.spv>");
        Console.WriteLine(
            "Runs only a supported synthetic compute fixture on an NVIDIA Vulkan device; maximum duration is 15 seconds.");
    }

    private static void WriteWorkerOutput(string stdout, string stderr)
    {
        if (stdout.Length != 0)
        {
            Console.Out.Write(stdout);
        }

        if (stderr.Length != 0)
        {
            Console.Error.Write(stderr);
        }
    }

    private sealed class ProbeFailureException(string message) : Exception(message);

    private enum ProbeFixture
    {
        Unknown,
        Exec,
        AtomicClamp,
    }
}
