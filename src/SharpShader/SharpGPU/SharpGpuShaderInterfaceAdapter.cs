using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.Compilation;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{
    public static class SharpGpuShaderInterfaceAdapter
    {
        public static SharpGpuRasterCompatibilityPlan CreateRasterCompatibilityPlan(
            ShaderInterfaceManifest manifest,
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException(
                    "Shader variant key must not be empty.",
                    nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException(
                    "Shader entry-point name must not be empty.",
                    nameof(entryPoint));
            }

            ShaderAttachmentInterface attachmentInterface = FindManifestEntry(
                manifest,
                variantKey,
                entryPoint,
                stage).AttachmentInterface;
            ShaderAttachmentPhase? phase = attachmentInterface.Phase;
            List<SharpGpuAttachmentCompatibilityFact> attachmentFacts = new();
            if (phase is not null)
            {
                foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
                {
                    attachmentFacts.Add(
                        new SharpGpuAttachmentCompatibilityFact(attachment));
                }
            }

            return new SharpGpuRasterCompatibilityPlan(
                attachmentInterface,
                CreateAttachmentInterfaceSignature(phase),
                attachmentFacts.ToArray());
        }

        public static Rhi.RHIRasterAttachmentShaderAbi
            QueryRasterAttachmentShaderAbi(
                Rhi.RHIDevice device,
                SharpGpuRasterCompatibilityPlan compatibilityPlan,
                in Rhi.RHIRasterPipelineDescriptor descriptor)
        {
            ArgumentNullException.ThrowIfNull(device);
            ArgumentNullException.ThrowIfNull(compatibilityPlan);
            compatibilityPlan.ValidateRasterPipelineDescriptor(in descriptor);
            Rhi.RHIPipelineLayout pipelineLayout = descriptor.PipelineLayout
                ?? throw new ArgumentException(
                    "A raster attachment shader ABI query requires a pipeline layout.",
                    nameof(descriptor));
            Rhi.RHIRasterAttachmentShaderAbiDescriptor abiDescriptor = new()
            {
                PipelineLayout = pipelineLayout,
                SampleCount = descriptor.SampleCount,
                ColorFormats = descriptor.ColorFormats,
                AttachmentInterface = descriptor.AttachmentInterface,
            };
            return device.QueryRasterAttachmentShaderAbi(in abiDescriptor);
        }

        public static Rhi.RHIRasterPipelineDescriptor BindRasterAttachmentShaderAbi(
            Rhi.RHIDevice device,
            SharpGpuRasterCompatibilityPlan compatibilityPlan,
            in Rhi.RHIRasterPipelineDescriptor descriptor)
        {
            Rhi.RHIRasterAttachmentShaderAbi abi =
                QueryRasterAttachmentShaderAbi(
                    device,
                    compatibilityPlan,
                    in descriptor);
            Rhi.RHIRasterAttachmentShaderAbiClaim claim = abi.CreateClaim();
            if (descriptor.AttachmentShaderAbiClaim.HasValue
                && descriptor.AttachmentShaderAbiClaim.Value != claim)
            {
                throw new ArgumentException(
                    "The raster pipeline already carries a different attachment shader ABI claim.",
                    nameof(descriptor));
            }
            Rhi.RHIRasterPipelineDescriptor result = descriptor;
            result.AttachmentShaderAbiClaim = claim;
            return result;
        }

        private static ShaderInterfaceEntry FindManifestEntry(
            ShaderInterfaceManifest manifest,
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage)
        {
            ShaderInterfaceVariant? selectedVariant = null;
            foreach (ShaderInterfaceVariant candidate in manifest.Variants)
            {
                if (!string.Equals(candidate.Key, variantKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedVariant is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader interface manifest contains more than one variant "
                        + $"named '{variantKey}'.");
                }

                selectedVariant = candidate;
            }

            if (selectedVariant is null)
            {
                throw new KeyNotFoundException(
                    $"Shader interface manifest does not contain variant "
                    + $"'{variantKey}'.");
            }

            ShaderInterfaceEntry? selectedEntry = null;
            foreach (ShaderInterfaceEntry candidate in selectedVariant.Entries)
            {
                if (candidate.Stage != stage
                    || !string.Equals(
                        candidate.Name,
                        entryPoint,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedEntry is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader variant '{variantKey}' contains more than one "
                        + $"entry named '{entryPoint}' for stage {stage}.");
                }

                selectedEntry = candidate;
            }

            return selectedEntry
                ?? throw new KeyNotFoundException(
                    $"Shader variant '{variantKey}' does not contain entry "
                    + $"'{entryPoint}' for stage {stage}.");
        }

        private static Rhi.RHIAttachmentInterfaceSignature
            CreateAttachmentInterfaceSignature(ShaderAttachmentPhase? phase)
        {
            if (phase is null)
            {
                return new Rhi.RHIAttachmentInterfaceSignature(
                    0,
                    Rhi.RHIAttachmentIndexArray.Empty,
                    Rhi.RHIAttachmentIndexArray.Empty);
            }

            int colorAttachmentCount = 0;
            int colorInputSlotCount = 0;
            int colorOutputLocationCount = 0;
            byte layeredAccessMask = 0;
            bool usesDualSourceColor = false;
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.Aspect != ShaderAttachmentAspect.Color)
                {
                    continue;
                }

                colorAttachmentCount = Math.Max(
                    colorAttachmentCount,
                    checked((int)attachment.LogicalAttachmentId + 1));
                if (attachment.InputIndex.HasValue)
                {
                    colorInputSlotCount = Math.Max(
                        colorInputSlotCount,
                        checked((int)attachment.InputIndex.Value + 1));
                }

                if (attachment.OutputLocation.HasValue)
                {
                    colorOutputLocationCount = Math.Max(
                        colorOutputLocationCount,
                        checked((int)attachment.OutputLocation.Value + 1));
                    usesDualSourceColor |= attachment.OutputIndex == 1;
                }

                bool usesSpecialAttachmentAccess = attachment.InputIndex.HasValue;
                if (attachment.LayerMode == ShaderAttachmentLayerMode.Layered
                    && usesSpecialAttachmentAccess)
                {
                    layeredAccessMask |= checked((byte)(
                        1u << checked((int)attachment.LogicalAttachmentId)));
                }
            }

            Rhi.RHIAttachmentIndexArray colorInputs =
                new(colorInputSlotCount);
            Rhi.RHIAttachmentIndexArray colorOutputs =
                new(colorOutputLocationCount);
            foreach (ShaderAttachmentDeclaration attachment in phase.Attachments)
            {
                if (attachment.Aspect != ShaderAttachmentAspect.Color)
                {
                    continue;
                }

                if (attachment.InputIndex.HasValue)
                {
                    colorInputs[checked((int)attachment.InputIndex.Value)] =
                        checked((int)attachment.LogicalAttachmentId);
                }

                if (attachment.OutputLocation.HasValue
                    && attachment.OutputIndex == 0)
                {
                    colorOutputs[checked((int)attachment.OutputLocation.Value)] =
                        checked((int)attachment.LogicalAttachmentId);
                }
            }

            Rhi.ERHISubPassFlags depthStencilFlags =
                Rhi.ERHISubPassFlags.None;
            if (phase.DepthStencilAccess == ShaderDepthStencilAccess.ReadOnly)
            {
                foreach (ShaderAttachmentDeclaration attachment in
                         phase.Attachments)
                {
                    if ((attachment.Aspect & ShaderAttachmentAspect.Color) != 0)
                    {
                        continue;
                    }

                    if ((attachment.Aspect & ShaderAttachmentAspect.Depth) != 0)
                    {
                        depthStencilFlags |= Rhi.ERHISubPassFlags.ReadOnlyDepth;
                    }

                    if ((attachment.Aspect & ShaderAttachmentAspect.Stencil) != 0)
                    {
                        depthStencilFlags |= Rhi.ERHISubPassFlags.ReadOnlyStencil;
                    }
                }
            }
            return new Rhi.RHIAttachmentInterfaceSignature(
                colorAttachmentCount,
                colorInputs,
                colorOutputs,
                depthStencilFlags,
                usesDualSourceColor,
                layeredAccessMask: layeredAccessMask);
        }
        public static SharpGpuBindingTableLayoutPlan CreateBindingTableLayoutPlan(
            ShaderInterfaceManifest manifest,
            string variantKey,
            string entryPoint,
            ShaderExecutionStage stage,
            Rhi.ERHIBackend backend,
            IReadOnlyDictionary<ShaderBindingKey, uint>? resolvedArrayCapacities = null)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            if (string.IsNullOrWhiteSpace(variantKey))
            {
                throw new ArgumentException("Shader variant key must not be empty.", nameof(variantKey));
            }

            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Shader entry-point name must not be empty.", nameof(entryPoint));
            }

            ShaderInterfaceVariant? selectedVariant = null;
            foreach (ShaderInterfaceVariant candidate in manifest.Variants)
            {
                if (!string.Equals(candidate.Key, variantKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedVariant is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader interface manifest contains more than one variant named '{variantKey}'.");
                }

                selectedVariant = candidate;
            }

            if (selectedVariant is null)
            {
                throw new KeyNotFoundException(
                    $"Shader interface manifest does not contain variant '{variantKey}'.");
            }

            ShaderInterfaceEntry? selectedEntry = null;
            foreach (ShaderInterfaceEntry candidate in selectedVariant.Entries)
            {
                if (candidate.Stage != stage
                    || !string.Equals(candidate.Name, entryPoint, StringComparison.Ordinal))
                {
                    continue;
                }

                if (selectedEntry is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader variant '{variantKey}' contains more than one entry named '{entryPoint}' for stage {stage}.");
                }

                selectedEntry = candidate;
            }

            if (selectedEntry is null)
            {
                throw new KeyNotFoundException(
                    $"Shader variant '{variantKey}' does not contain entry '{entryPoint}' for stage {stage}.");
            }

            ShaderInterfaceLayout? logicalLayout = null;
            foreach (ShaderInterfaceLayout candidate in manifest.LogicalLayouts)
            {
                if (candidate.Signature != selectedEntry.LogicalLayoutSignature)
                {
                    continue;
                }

                if (logicalLayout is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader interface manifest contains more than one logical layout with signature {selectedEntry.LogicalLayoutSignature}.");
                }

                logicalLayout = candidate;
            }

            if (logicalLayout is null)
            {
                throw new KeyNotFoundException(
                    $"Shader entry '{entryPoint}' ({stage}) references missing logical layout {selectedEntry.LogicalLayoutSignature}.");
            }

            ShaderBackendLayouts? backendLayouts = null;
            foreach (ShaderBackendLayouts candidate in manifest.BackendLayouts)
            {
                if (candidate.LogicalLayoutSignature != selectedEntry.LogicalLayoutSignature)
                {
                    continue;
                }

                if (backendLayouts is not null)
                {
                    throw new InvalidOperationException(
                        $"Shader interface manifest contains more than one backend layout for logical layout {selectedEntry.LogicalLayoutSignature}.");
                }

                backendLayouts = candidate;
            }

            if (backendLayouts is null)
            {
                throw new KeyNotFoundException(
                    $"Shader entry '{entryPoint}' ({stage}) references logical layout {selectedEntry.LogicalLayoutSignature} without backend mappings.");
            }

            return CreateBindingTableLayoutPlan(
                logicalLayout,
                backendLayouts,
                backend,
                resolvedArrayCapacities);
        }

        public static SharpGpuBindingTableLayoutPlan CreateBindingTableLayoutPlan(
            ShaderInterfaceLayout logicalLayout,
            ShaderBackendLayouts backendLayouts,
            Rhi.ERHIBackend backend,
            IReadOnlyDictionary<ShaderBindingKey, uint>? resolvedArrayCapacities = null)
        {
            ArgumentNullException.ThrowIfNull(logicalLayout);
            ArgumentNullException.ThrowIfNull(backendLayouts);
            if (backend is Rhi.ERHIBackend.Pending || !Enum.IsDefined(backend))
            {
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "SharpGPU backend is not defined.");
            }

            if (backendLayouts.LogicalLayoutSignature != logicalLayout.Signature)
            {
                throw new ArgumentException(
                    "Backend mappings reference a different logical shader layout signature.",
                    nameof(backendLayouts));
            }

            Dictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings =
                MaterializeLogicalBindings(logicalLayout);
            ValidateArrayCapacities(logicalBindings, resolvedArrayCapacities);

            Dictionary<ShaderBindingKey, uint> resolvedCounts =
                ResolveBindingCounts(logicalBindings, backendLayouts.Metal, resolvedArrayCapacities);

            List<PendingBinding> pending = backend switch
            {
                Rhi.ERHIBackend.DirectX12 => BuildDx12Bindings(
                    logicalBindings,
                    RequireBackendLayout(backendLayouts.Dx12, "DX12"),
                    resolvedCounts,
                    backend),
                Rhi.ERHIBackend.Vulkan => BuildVulkanBindings(
                    logicalBindings,
                    RequireBackendLayout(backendLayouts.Vulkan, "Vulkan"),
                    resolvedCounts,
                    backend),
                Rhi.ERHIBackend.Metal => BuildMetalBindings(
                    logicalBindings,
                    RequireBackendLayout(backendLayouts.Metal, "Metal"),
                    resolvedCounts,
                    backend),
                _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "SharpGPU backend is not supported."),
            };

            ValidateCoverage(logicalBindings, pending, backend);
            return MaterializePlan(backend, logicalLayout.Signature, pending);
        }

        private static T RequireBackendLayout<T>(T? layout, string name)
            where T : class
        {
            return layout
                ?? throw new ArgumentException(
                    $"The shader interface manifest does not contain a {name} backend layout.",
                    nameof(layout));
        }

        private static Dictionary<ShaderBindingKey, ShaderLogicalBinding> MaterializeLogicalBindings(
            ShaderInterfaceLayout layout)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> bindings =
                new(layout.Bindings.Count);
            foreach (ShaderLogicalBinding binding in layout.Bindings)
            {
                if (!bindings.TryAdd(binding.Key, binding))
                {
                    throw new ArgumentException(
                        $"Logical shader layout contains duplicate binding {binding.Key}.",
                        nameof(layout));
                }
            }

            return bindings;
        }

        private static void ValidateArrayCapacities(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            IReadOnlyDictionary<ShaderBindingKey, uint>? capacities)
        {
            if (capacities is null)
            {
                return;
            }

            foreach (KeyValuePair<ShaderBindingKey, uint> capacity in capacities)
            {
                if (!logicalBindings.TryGetValue(capacity.Key, out ShaderLogicalBinding? binding))
                {
                    throw new ArgumentException(
                        $"Array capacity references unknown logical binding {capacity.Key}.",
                        nameof(capacities));
                }

                if (binding.Shape.Array.BoundedElementCount.HasValue)
                {
                    throw new ArgumentException(
                        $"Array capacity for statically bounded binding {capacity.Key} must not be overridden.",
                        nameof(capacities));
                }

                if (capacity.Value == 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(capacities),
                        $"Array capacity for binding {capacity.Key} must be positive.");
                }
            }
        }

        private static Dictionary<ShaderBindingKey, uint> ResolveBindingCounts(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            MetalShaderBackendLayout? metalLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint>? capacities)
        {
            Dictionary<ShaderBindingKey, uint> metalReferenceCounts = new();
            if (metalLayout is not null)
            {
                foreach (MetalReferenceBufferBindingMapping mapping in metalLayout.ReferenceBufferBindings)
                {
                    metalReferenceCounts.Add(mapping.LogicalBinding, mapping.ReferenceCount);
                }
            }

            Dictionary<ShaderBindingKey, uint> result = new(logicalBindings.Count);
            foreach (KeyValuePair<ShaderBindingKey, ShaderLogicalBinding> pair in logicalBindings)
            {
                ShaderLogicalBinding binding = pair.Value;
                uint? boundedCount = binding.Shape.Array.BoundedElementCount;
                if (boundedCount.HasValue)
                {
                    result.Add(pair.Key, boundedCount.Value);
                    continue;
                }

                uint count;
                if (capacities is not null && capacities.TryGetValue(pair.Key, out uint explicitCapacity))
                {
                    count = explicitCapacity;
                }
                else if (metalReferenceCounts.TryGetValue(pair.Key, out uint manifestCapacity))
                {
                    count = manifestCapacity;
                }
                else
                {
                    throw new ArgumentException(
                        $"Runtime or specialization-sized binding {pair.Key} requires a positive flattened capacity.",
                        nameof(capacities));
                }

                if (metalReferenceCounts.TryGetValue(pair.Key, out uint expectedCount)
                    && expectedCount != count)
                {
                    throw new ArgumentException(
                        $"Array capacity {count} for binding {pair.Key} does not match the compiled Metal reference count {expectedCount}.",
                        nameof(capacities));
                }

                result.Add(pair.Key, count);
            }

            return result;
        }

        private static List<PendingBinding> BuildDx12Bindings(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            Dx12ShaderBackendLayout backendLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint> counts,
            Rhi.ERHIBackend backend)
        {
            List<PendingBinding> result = new(backendLayout.Bindings.Count);
            foreach (Dx12ShaderBindingMapping mapping in backendLayout.Bindings)
            {
                ShaderLogicalBinding logical = GetLogicalBinding(logicalBindings, mapping.LogicalBinding, "DX12");
                if (mapping.RegisterSpace != logical.Key.Table
                    || mapping.ShaderRegister != logical.Key.Slot
                    || mapping.RegisterClass != logical.Key.Type)
                {
                    throw new ArgumentException(
                        $"DX12 mapping for {logical.Key} does not preserve register space, register, and class.",
                        nameof(backendLayout));
                }

                result.Add(CreatePendingBinding(
                    logical,
                    mapping.RegisterSpace,
                    mapping.ShaderRegister,
                    counts[logical.Key],
                    backend,
                    order: mapping.ShaderRegister));
            }

            return result;
        }

        private static List<PendingBinding> BuildVulkanBindings(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            VulkanShaderBackendLayout backendLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint> counts,
            Rhi.ERHIBackend backend)
        {
            List<PendingBinding> result = new(backendLayout.Bindings.Count);
            uint currentSet = 0;
            uint expectedBinding = 0;
            bool hasSet = false;
            foreach (VulkanShaderBindingMapping mapping in backendLayout.Bindings)
            {
                ShaderLogicalBinding logical = GetLogicalBinding(logicalBindings, mapping.LogicalBinding, "Vulkan");
                if (!hasSet || mapping.DescriptorSet != currentSet)
                {
                    uint expectedSet = hasSet ? checked(currentSet + 1) : 0;
                    if (mapping.DescriptorSet != expectedSet)
                    {
                        throw new ArgumentException(
                            $"Vulkan physical descriptor sets must be dense; expected set {expectedSet}, got {mapping.DescriptorSet}.",
                            nameof(backendLayout));
                    }

                    currentSet = mapping.DescriptorSet;
                    expectedBinding = 0;
                    hasSet = true;
                }

                if (mapping.Binding != expectedBinding)
                {
                    throw new ArgumentException(
                        $"Vulkan descriptor set {mapping.DescriptorSet} must use dense bindings; expected {expectedBinding}, got {mapping.Binding}.",
                        nameof(backendLayout));
                }

                ValidateVulkanDescriptorKind(logical, mapping.DescriptorKind);
                result.Add(CreatePendingBinding(
                    logical,
                    mapping.DescriptorSet,
                    mapping.Binding,
                    counts[logical.Key],
                    backend,
                    order: mapping.Binding));
                expectedBinding = checked(expectedBinding + 1);
            }

            return result;
        }

        private static List<PendingBinding> BuildMetalBindings(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            MetalShaderBackendLayout backendLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint> counts,
            Rhi.ERHIBackend backend)
        {
            if (backendLayout.DirectBindings.Count != 0
                && backendLayout.ReferenceBufferBindings.Count != 0)
            {
                throw new ArgumentException(
                    "Metal backend layout must use either direct bindings or reference buffers, never both.",
                    nameof(backendLayout));
            }

            if (backendLayout.ReferenceBufferBindings.Count == 0)
            {
                return BuildMetalDirectBindings(logicalBindings, backendLayout, counts, backend);
            }

            return BuildMetalReferenceBindings(logicalBindings, backendLayout, counts, backend);
        }

        private static List<PendingBinding> BuildMetalDirectBindings(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            MetalShaderBackendLayout backendLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint> counts,
            Rhi.ERHIBackend backend)
        {
            List<PendingBinding> result = new(backendLayout.DirectBindings.Count);
            Dictionary<ShaderPhysicalBindingNamespace, uint> nextIndex = new();
            foreach (MetalDirectBindingMapping mapping in backendLayout.DirectBindings)
            {
                ShaderLogicalBinding logical = GetLogicalBinding(logicalBindings, mapping.LogicalBinding, "Metal");
                ShaderPhysicalBindingNamespace expectedNamespace = GetMetalNamespace(logical);
                uint expectedIndex = nextIndex.TryGetValue(expectedNamespace, out uint index)
                    ? index
                    : 0;
                if (mapping.BindingTable != MetalShaderBackendLayout.RootBindingTable
                    || mapping.Namespace != expectedNamespace
                    || mapping.Index != expectedIndex)
                {
                    throw new ArgumentException(
                        $"Metal direct mapping {logical.Key} must use root table 0 and dense {expectedNamespace} index {expectedIndex}.",
                        nameof(backendLayout));
                }

                uint count = counts[logical.Key];
                if (count != 1)
                {
                    throw new ArgumentException(
                        $"Metal descriptor array {logical.Key} must use reference-buffer mode.",
                        nameof(backendLayout));
                }

                result.Add(CreatePendingBinding(
                    logical,
                    MetalShaderBackendLayout.RootBindingTable,
                    mapping.Index,
                    count,
                    backend,
                    order: mapping.Index));
                nextIndex[expectedNamespace] = checked(expectedIndex + 1);
            }

            return result;
        }

        private static List<PendingBinding> BuildMetalReferenceBindings(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            MetalShaderBackendLayout backendLayout,
            IReadOnlyDictionary<ShaderBindingKey, uint> counts,
            Rhi.ERHIBackend backend)
        {
            List<PendingBinding> result = new(backendLayout.ReferenceBufferBindings.Count);
            uint currentBuffer = 0;
            ulong expectedByteOffset = 0;
            bool hasBuffer = false;
            foreach (MetalReferenceBufferBindingMapping mapping in backendLayout.ReferenceBufferBindings)
            {
                ShaderLogicalBinding logical = GetLogicalBinding(logicalBindings, mapping.LogicalBinding, "Metal");
                if (!hasBuffer || mapping.ReferenceBufferIndex != currentBuffer)
                {
                    uint expectedBuffer = hasBuffer ? checked(currentBuffer + 1) : 0;
                    if (mapping.ReferenceBufferIndex != expectedBuffer)
                    {
                        throw new ArgumentException(
                            $"Metal reference-buffer indices must be dense; expected {expectedBuffer}, got {mapping.ReferenceBufferIndex}.",
                            nameof(backendLayout));
                    }

                    currentBuffer = mapping.ReferenceBufferIndex;
                    expectedByteOffset = 0;
                    hasBuffer = true;
                }

                ShaderPhysicalBindingNamespace expectedNamespace = GetMetalNamespace(logical);
                uint count = counts[logical.Key];
                if (mapping.BindingTable != MetalShaderBackendLayout.RootBindingTable
                    || mapping.ResourceNamespace != expectedNamespace
                    || mapping.ReferenceCount != count
                    || mapping.ByteOffset != expectedByteOffset)
                {
                    throw new ArgumentException(
                        $"Metal reference mapping {logical.Key} does not match its dense root-buffer slot, namespace, count, or byte offset.",
                        nameof(backendLayout));
                }

                ulong referenceIndex = mapping.ByteOffset / MetalReferenceBufferBindingMapping.ReferenceByteSize;
                if (referenceIndex > uint.MaxValue)
                {
                    throw new OverflowException(
                        $"Metal reference-buffer slot for {logical.Key} exceeds SharpGPU's 32-bit slot range.");
                }

                result.Add(CreatePendingBinding(
                    logical,
                    mapping.ReferenceBufferIndex,
                    (uint)referenceIndex,
                    count,
                    backend,
                    order: referenceIndex));
                expectedByteOffset = checked(
                    expectedByteOffset
                    + checked((ulong)count * MetalReferenceBufferBindingMapping.ReferenceByteSize));
            }

            return result;
        }

        private static PendingBinding CreatePendingBinding(
            ShaderLogicalBinding logical,
            uint table,
            uint slot,
            uint count,
            Rhi.ERHIBackend backend,
            ulong order)
        {
            if (count == 0 || count > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(count),
                    count,
                    $"SharpGPU binding {logical.Key} count must fit a positive Int32-backed binding table.");
            }

            return new PendingBinding(
                logical.Key,
                table,
                slot,
                count,
                ConvertBindType(logical, backend),
                ConvertShaderStages(logical.StageMask, backend),
                order);
        }

        private static Rhi.ERHIBindType ConvertBindType(
            ShaderLogicalBinding binding,
            Rhi.ERHIBackend backend)
        {
            ShaderResourceShape shape = binding.Shape;
            return shape.Kind switch
            {
                ShaderResourceKind.ConstantBuffer => Rhi.ERHIBindType.UniformBuffer,
                ShaderResourceKind.Sampler => Rhi.ERHIBindType.Sampler,
                ShaderResourceKind.AccelerationStructure => Rhi.ERHIBindType.AccelStruct,
                ShaderResourceKind.StructuredBuffer
                    or ShaderResourceKind.StorageBuffer
                    or ShaderResourceKind.ByteAddressBuffer
                    or ShaderResourceKind.AtomicCounter
                    or ShaderResourceKind.ShaderRecordBuffer => binding.Key.Type switch
                    {
                        ShaderBindingClass.ShaderResource => Rhi.ERHIBindType.Buffer,
                        ShaderBindingClass.UnorderedAccess => Rhi.ERHIBindType.StorageBuffer,
                        _ => throw UnsupportedBinding(binding, "buffer binding class"),
                    },
                ShaderResourceKind.Texture => ConvertTextureBindType(binding),
                ShaderResourceKind.TypedBuffer => throw UnsupportedBinding(
                    binding,
                    "SharpGPU does not expose Vulkan uniform/storage texel-buffer descriptors"),
                ShaderResourceKind.InputAttachment => throw UnsupportedBinding(
                    binding,
                    "SharpGPU does not expose input-attachment descriptors"),
                ShaderResourceKind.FeedbackTexture => backend == Rhi.ERHIBackend.DirectX12
                    ? ConvertFeedbackTextureBindType(binding)
                    : throw UnsupportedBinding(
                        binding,
                        "SharpGPU does not expose sampler-feedback texture descriptors"),
                _ => throw UnsupportedBinding(binding, "resource kind"),
            };
        }

        private static Rhi.ERHIBindType ConvertFeedbackTextureBindType(
            ShaderLogicalBinding binding)
        {
            return binding.Shape.Dimension switch
            {
                ShaderResourceDimension.Texture2D => Rhi.ERHIBindType.StorageTexture2D,
                ShaderResourceDimension.Texture2DArray =>
                    Rhi.ERHIBindType.StorageTexture2DArray,
                _ => throw UnsupportedBinding(
                    binding,
                    "DX12 sampler-feedback bindings require Texture2D or Texture2DArray"),
            };
        }

        private static Rhi.ERHIBindType ConvertTextureBindType(ShaderLogicalBinding binding)
        {
            bool writable = binding.Key.Type == ShaderBindingClass.UnorderedAccess;
            return (binding.Shape.Dimension, writable) switch
            {
                (ShaderResourceDimension.Texture2D, false) => Rhi.ERHIBindType.Texture2D,
                (ShaderResourceDimension.Texture2D, true) => Rhi.ERHIBindType.StorageTexture2D,
                (ShaderResourceDimension.Texture2DArray, false) => Rhi.ERHIBindType.Texture2DArray,
                (ShaderResourceDimension.Texture2DArray, true) => Rhi.ERHIBindType.StorageTexture2DArray,
                (ShaderResourceDimension.Texture2DMultisampled, false) => Rhi.ERHIBindType.Texture2DMS,
                (ShaderResourceDimension.Texture2DMultisampled, true) => Rhi.ERHIBindType.StorageTexture2DMS,
                (ShaderResourceDimension.Texture2DMultisampledArray, false) => Rhi.ERHIBindType.Texture2DArrayMS,
                (ShaderResourceDimension.Texture2DMultisampledArray, true) => Rhi.ERHIBindType.StorageTexture2DArrayMS,
                (ShaderResourceDimension.Texture3D, false) => Rhi.ERHIBindType.Texture3D,
                (ShaderResourceDimension.Texture3D, true) => Rhi.ERHIBindType.StorageTexture3D,
                (ShaderResourceDimension.TextureCube, false) => Rhi.ERHIBindType.TextureCube,
                (ShaderResourceDimension.TextureCube, true) => Rhi.ERHIBindType.StorageTextureCube,
                (ShaderResourceDimension.TextureCubeArray, false) => Rhi.ERHIBindType.TextureCubeArray,
                (ShaderResourceDimension.TextureCubeArray, true) => Rhi.ERHIBindType.StorageTextureCubeArray,
                (ShaderResourceDimension.Texture1D, _) or
                    (ShaderResourceDimension.Texture1DArray, _) => throw UnsupportedBinding(
                        binding,
                        "SharpGPU does not expose Texture1D argument-table bind types"),
                _ => throw UnsupportedBinding(binding, "texture dimension or access"),
            };
        }

        private static Rhi.ERHIShaderStageMask ConvertShaderStages(
            ShaderStageMask stages,
            Rhi.ERHIBackend backend)
        {
            ShaderStageMaskUtility.Validate(stages);
            const ShaderStageMask unsupportedGraphics =
                ShaderStageMask.Hull | ShaderStageMask.Domain | ShaderStageMask.Geometry;
            if ((stages & unsupportedGraphics) != 0)
            {
                throw new NotSupportedException(
                    $"SharpGPU does not expose hull, domain, or geometry shader stages ({stages}).");
            }

            if ((stages & ShaderStageMask.Node) != 0)
            {
                if (backend == Rhi.ERHIBackend.DirectX12 && stages == ShaderStageMask.Node)
                {
                    return Rhi.ERHIShaderStageMask.All;
                }

                throw new NotSupportedException(
                    $"Shader node-stage visibility is only representable by the DX12 Work Graph backend ({stages}, {backend}).");
            }

            Rhi.ERHIShaderStageMask result = Rhi.ERHIShaderStageMask.None;
            if ((stages & ShaderStageMask.Vertex) != 0)
                result |= Rhi.ERHIShaderStageMask.Vertex;
            if ((stages & ShaderStageMask.Pixel) != 0)
                result |= Rhi.ERHIShaderStageMask.Fragment;
            if ((stages & ShaderStageMask.Compute) != 0)
                result |= Rhi.ERHIShaderStageMask.Compute;
            if ((stages & ShaderStageMask.Amplification) != 0)
                result |= Rhi.ERHIShaderStageMask.Task;
            if ((stages & ShaderStageMask.Mesh) != 0)
                result |= Rhi.ERHIShaderStageMask.Mesh;

            const ShaderStageMask ray =
                ShaderStageMask.RayGeneration
                | ShaderStageMask.Intersection
                | ShaderStageMask.AnyHit
                | ShaderStageMask.ClosestHit
                | ShaderStageMask.Miss
                | ShaderStageMask.Callable;
            if ((stages & ray) != 0)
            {
                result |= Rhi.ERHIShaderStageMask.RayTracing;
            }

            return result;
        }

        private static void ValidateVulkanDescriptorKind(
            ShaderLogicalBinding logical,
            VulkanDescriptorKind actual)
        {
            VulkanDescriptorKind expected = logical.Shape.Kind switch
            {
                ShaderResourceKind.ConstantBuffer => VulkanDescriptorKind.UniformBuffer,
                ShaderResourceKind.Sampler => VulkanDescriptorKind.Sampler,
                ShaderResourceKind.Texture => logical.Shape.Access == ShaderResourceAccess.ReadOnly
                    ? VulkanDescriptorKind.SampledImage
                    : VulkanDescriptorKind.StorageImage,
                ShaderResourceKind.StructuredBuffer
                    or ShaderResourceKind.StorageBuffer
                    or ShaderResourceKind.ByteAddressBuffer
                    or ShaderResourceKind.AtomicCounter
                    or ShaderResourceKind.ShaderRecordBuffer => VulkanDescriptorKind.StorageBuffer,
                ShaderResourceKind.AccelerationStructure => VulkanDescriptorKind.AccelerationStructure,
                ShaderResourceKind.TypedBuffer => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose Vulkan uniform/storage texel-buffer descriptors"),
                ShaderResourceKind.InputAttachment => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose input-attachment descriptors"),
                ShaderResourceKind.FeedbackTexture => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose sampler-feedback texture descriptors"),
                _ => throw UnsupportedBinding(logical, "Vulkan descriptor kind"),
            };

            if (actual != expected)
            {
                throw new ArgumentException(
                    $"Vulkan binding {logical.Key} requires descriptor kind {expected}, not {actual}.",
                    nameof(actual));
            }
        }

        private static ShaderPhysicalBindingNamespace GetMetalNamespace(ShaderLogicalBinding logical)
        {
            return logical.Shape.Kind switch
            {
                ShaderResourceKind.Sampler => ShaderPhysicalBindingNamespace.Sampler,
                ShaderResourceKind.Texture => ShaderPhysicalBindingNamespace.Texture,
                ShaderResourceKind.ConstantBuffer
                    or ShaderResourceKind.StructuredBuffer
                    or ShaderResourceKind.StorageBuffer
                    or ShaderResourceKind.ByteAddressBuffer
                    or ShaderResourceKind.AccelerationStructure
                    or ShaderResourceKind.AtomicCounter
                    or ShaderResourceKind.ShaderRecordBuffer => ShaderPhysicalBindingNamespace.Buffer,
                ShaderResourceKind.TypedBuffer => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose Vulkan texel-buffer compatible bindings"),
                ShaderResourceKind.InputAttachment => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose input-attachment descriptors"),
                ShaderResourceKind.FeedbackTexture => throw UnsupportedBinding(
                    logical,
                    "SharpGPU does not expose sampler-feedback texture descriptors"),
                _ => throw UnsupportedBinding(logical, "Metal binding namespace"),
            };
        }

        private static ShaderLogicalBinding GetLogicalBinding(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            ShaderBindingKey key,
            string backend)
        {
            return logicalBindings.TryGetValue(key, out ShaderLogicalBinding? binding)
                ? binding
                : throw new ArgumentException(
                    $"{backend} backend mapping references unknown logical binding {key}.");
        }

        private static NotSupportedException UnsupportedBinding(
            ShaderLogicalBinding binding,
            string reason)
        {
            return new NotSupportedException(
                $"Logical binding {binding.Key} ({binding.Shape.Kind}, {binding.Shape.Dimension}, {binding.Shape.Access}) cannot be represented by SharpGPU: {reason}.");
        }

        private static void ValidateCoverage(
            IReadOnlyDictionary<ShaderBindingKey, ShaderLogicalBinding> logicalBindings,
            IReadOnlyList<PendingBinding> pending,
            Rhi.ERHIBackend backend)
        {
            if (pending.Count != logicalBindings.Count)
            {
                throw new ArgumentException(
                    $"{backend} mapping covers {pending.Count} bindings, but the logical layout contains {logicalBindings.Count}.");
            }

            HashSet<ShaderBindingKey> seen = new();
            foreach (PendingBinding binding in pending)
            {
                if (!seen.Add(binding.LogicalBinding))
                {
                    throw new ArgumentException(
                        $"{backend} mapping contains duplicate logical binding {binding.LogicalBinding}.");
                }
            }
        }

        private static SharpGpuBindingTableLayoutPlan MaterializePlan(
            Rhi.ERHIBackend backend,
            ShaderLayoutSignature logicalLayoutSignature,
            List<PendingBinding> pending)
        {
            pending.Sort(PendingBindingComparer.Instance);

            List<SharpGpuBindingTableLayoutPlan.PhysicalTable> tables = new();
            List<SharpGpuBindingLocation> bindings = new(pending.Count);
            int pendingIndex = 0;
            while (pendingIndex < pending.Count)
            {
                uint tableIndex = pending[pendingIndex].TableIndex;
                int tableStart = pendingIndex;
                while (pendingIndex < pending.Count
                    && pending[pendingIndex].TableIndex == tableIndex)
                {
                    ++pendingIndex;
                }

                int elementCount = pendingIndex - tableStart;
                Rhi.RHIBindingTableLayoutElement[] elements =
                    new Rhi.RHIBindingTableLayoutElement[elementCount];
                for (int elementIndex = 0; elementIndex < elementCount; ++elementIndex)
                {
                    PendingBinding binding = pending[tableStart + elementIndex];
                    elements[elementIndex] = new Rhi.RHIBindingTableLayoutElement
                    {
                        Slot = binding.Slot,
                        Count = binding.Count,
                        Type = binding.BindType,
                        Stages = binding.ShaderStages,
                    };
                    bindings.Add(new SharpGpuBindingLocation(
                        binding.LogicalBinding,
                        binding.TableIndex,
                        binding.Slot,
                        binding.Count,
                        binding.BindType,
                        binding.ShaderStages,
                        elementIndex));
                }

                tables.Add(new SharpGpuBindingTableLayoutPlan.PhysicalTable(tableIndex, elements));
            }

            bindings.Sort(static (left, right) => CompareLogicalKeys(
                left.LogicalBinding,
                right.LogicalBinding));
            return new SharpGpuBindingTableLayoutPlan(
                backend,
                logicalLayoutSignature,
                bindings.ToArray(),
                tables.ToArray());
        }

        private static int CompareLogicalKeys(ShaderBindingKey left, ShaderBindingKey right)
        {
            int table = left.Table.CompareTo(right.Table);
            if (table != 0)
            {
                return table;
            }

            int slot = left.Slot.CompareTo(right.Slot);
            return slot != 0 ? slot : left.Type.CompareTo(right.Type);
        }

        private readonly struct PendingBinding
        {
            public ShaderBindingKey LogicalBinding { get; }
            public uint TableIndex { get; }
            public uint Slot { get; }
            public uint Count { get; }
            public Rhi.ERHIBindType BindType { get; }
            public Rhi.ERHIShaderStageMask ShaderStages { get; }
            public ulong Order { get; }

            public PendingBinding(
                ShaderBindingKey logicalBinding,
                uint tableIndex,
                uint slot,
                uint count,
                Rhi.ERHIBindType bindType,
                Rhi.ERHIShaderStageMask shaderStages,
                ulong order)
            {
                LogicalBinding = logicalBinding;
                TableIndex = tableIndex;
                Slot = slot;
                Count = count;
                BindType = bindType;
                ShaderStages = shaderStages;
                Order = order;
            }
        }

        private sealed class PendingBindingComparer : IComparer<PendingBinding>
        {
            public static PendingBindingComparer Instance { get; } = new();

            public int Compare(PendingBinding left, PendingBinding right)
            {
                int table = left.TableIndex.CompareTo(right.TableIndex);
                if (table != 0)
                {
                    return table;
                }

                int order = left.Order.CompareTo(right.Order);
                if (order != 0)
                {
                    return order;
                }

                return CompareLogicalKeys(left.LogicalBinding, right.LogicalBinding);
            }
        }
    }
}
