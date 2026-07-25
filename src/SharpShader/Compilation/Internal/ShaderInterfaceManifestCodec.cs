using System;
using System.Collections.Generic;

namespace SharpShader.Compilation.Internal
{
    internal static class ShaderInterfaceManifestCodec
    {
        public static ShaderInterfaceManifestDocument Encode(ShaderInterfaceManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);

            return new ShaderInterfaceManifestDocument
            {
                SchemaVersion = manifest.SchemaVersion,
                SourceDigest = manifest.SourceDigest,
                Targets = EncodeTargets(manifest.Targets),
                Toolchain = Map(manifest.ToolchainComponents, EncodeToolchainComponent),
                Layouts = Map(manifest.LogicalLayouts, EncodeLayout),
                Variants = Map(manifest.Variants, EncodeVariant),
                BackendLayouts = Map(manifest.BackendLayouts, EncodeBackendLayouts),
            };
        }

        public static ShaderInterfaceManifest Decode(ShaderInterfaceManifestDocument document)
        {
            ArgumentNullException.ThrowIfNull(document);

            ShaderToolchainComponentDocument[] toolchainDocuments = RequireArray(
                document.Toolchain,
                "toolchain");
            ShaderInterfaceLayoutDocument[] layoutDocuments = RequireArray(document.Layouts, "layouts");
            ShaderInterfaceVariantDocument[] variantDocuments = RequireArray(document.Variants, "variants");
            ShaderBackendLayoutsDocument[] backendDocuments = RequireArray(
                document.BackendLayouts,
                "backendLayouts");

            return new ShaderInterfaceManifest(
                RequireString(document.SourceDigest, "sourceDigest"),
                Map(toolchainDocuments, DecodeToolchainComponent),
                Map(layoutDocuments, DecodeLayout),
                Map(variantDocuments, DecodeVariant),
                Map(backendDocuments, DecodeBackendLayouts),
                DecodeTargets(RequireArray(document.Targets, "targets")),
                RequireValue(document.SchemaVersion, "schemaVersion"));
        }

        private static ShaderToolchainComponentDocument EncodeToolchainComponent(
            ShaderToolchainComponent component)
        {
            return new ShaderToolchainComponentDocument
            {
                Name = component.Name,
                Version = component.Version,
                ContentDigest = component.ContentDigest,
            };
        }

        private static ShaderToolchainComponent DecodeToolchainComponent(
            ShaderToolchainComponentDocument document)
        {
            return new ShaderToolchainComponent(
                RequireString(document.Name, "toolchain[].name"),
                RequireString(document.Version, "toolchain[].version"),
                document.ContentDigest);
        }

        private static ShaderInterfaceLayoutDocument EncodeLayout(ShaderInterfaceLayout layout)
        {
            return new ShaderInterfaceLayoutDocument
            {
                Signature = layout.Signature.ToString(),
                Bindings = Map(layout.Bindings, EncodeLogicalBinding),
            };
        }

        private static ShaderInterfaceLayout DecodeLayout(ShaderInterfaceLayoutDocument document)
        {
            ShaderLogicalBinding[] bindings = Map(
                RequireArray(document.Bindings, "layouts[].bindings"),
                DecodeLogicalBinding);
            ShaderInterfaceLayout layout = new(bindings);
            ShaderLayoutSignature declaredSignature = ParseSignature(
                document.Signature,
                "layouts[].signature");
            if (declaredSignature != layout.Signature)
            {
                throw new System.Text.Json.JsonException(
                    $"Layout signature {declaredSignature} does not match the canonical ABI signature {layout.Signature}.");
            }

            return layout;
        }

        private static ShaderLogicalBindingDocument EncodeLogicalBinding(ShaderLogicalBinding binding)
        {
            return new ShaderLogicalBindingDocument
            {
                Key = EncodeBindingKey(binding.Key),
                CanonicalName = binding.CanonicalName,
                Aliases = Copy(binding.Aliases),
                Shape = EncodeResourceShape(binding.Shape),
                Stages = EncodeStages(binding.StageMask),
                Provenance = binding.Provenance.ToString(),
                ConstantBuffer = binding.ConstantBufferLayout is null
                    ? null
                    : EncodeConstantBuffer(binding.ConstantBufferLayout),
            };
        }

        private static ShaderLogicalBinding DecodeLogicalBinding(ShaderLogicalBindingDocument document)
        {
            return new ShaderLogicalBinding(
                DecodeBindingKey(RequireObject(document.Key, "layouts[].bindings[].key")),
                RequireString(document.CanonicalName, "layouts[].bindings[].canonicalName"),
                RequireArray(document.Aliases, "layouts[].bindings[].aliases"),
                DecodeResourceShape(RequireObject(document.Shape, "layouts[].bindings[].shape")),
                DecodeStages(RequireArray(document.Stages, "layouts[].bindings[].stages")),
                document.ConstantBuffer is null ? null : DecodeConstantBuffer(document.ConstantBuffer),
                ParseEnum<ShaderBindingProvenance>(
                    document.Provenance,
                    "layouts[].bindings[].provenance"));
        }

        private static ShaderBindingKeyDocument EncodeBindingKey(ShaderBindingKey key)
        {
            return new ShaderBindingKeyDocument
            {
                Table = key.Table,
                Slot = key.Slot,
                Type = key.Type.ToString(),
            };
        }

        private static ShaderBindingKey DecodeBindingKey(ShaderBindingKeyDocument document)
        {
            return new ShaderBindingKey(
                RequireValue(document.Table, "bindingKey.table"),
                RequireValue(document.Slot, "bindingKey.slot"),
                ParseEnum<ShaderBindingClass>(document.Type, "bindingKey.type"));
        }

        private static ShaderResourceShapeDocument EncodeResourceShape(ShaderResourceShape shape)
        {
            return new ShaderResourceShapeDocument
            {
                Kind = shape.Kind.ToString(),
                Dimension = shape.Dimension.ToString(),
                Access = shape.Access.ToString(),
                Array = Map(shape.Array.Extents, EncodeArrayExtent),
                StructureStride = shape.StructureStride,
                SamplerKind = shape.SamplerKind?.ToString(),
                CounterKind = shape.CounterKind.ToString(),
            };
        }

        private static ShaderResourceShape DecodeResourceShape(ShaderResourceShapeDocument document)
        {
            return new ShaderResourceShape(
                ParseEnum<ShaderResourceKind>(document.Kind, "resourceShape.kind"),
                ParseEnum<ShaderResourceDimension>(document.Dimension, "resourceShape.dimension"),
                ParseEnum<ShaderResourceAccess>(document.Access, "resourceShape.access"),
                DecodeArrayShape(RequireArray(document.Array, "resourceShape.array")),
                document.StructureStride,
                ParseNullableEnum<ShaderSamplerKind>(document.SamplerKind, "resourceShape.samplerKind"),
                ParseEnum<ShaderCounterKind>(document.CounterKind, "resourceShape.counterKind"));
        }

        private static ShaderArrayExtentDocument EncodeArrayExtent(ShaderArrayExtent extent)
        {
            return new ShaderArrayExtentDocument
            {
                Kind = extent.Kind.ToString(),
                Value = extent.Value,
            };
        }

        private static ShaderArrayShape DecodeArrayShape(ShaderArrayExtentDocument[] documents)
        {
            ShaderArrayExtent[] extents = new ShaderArrayExtent[documents.Length];
            for (int index = 0; index < documents.Length; ++index)
            {
                ShaderArrayExtentDocument document = RequireObject(
                    documents[index],
                    "array[]");
                ShaderArrayExtentKind kind = ParseEnum<ShaderArrayExtentKind>(
                    document.Kind,
                    "array[].kind");
                uint value = RequireValue(document.Value, "array[].value");
                extents[index] = kind switch
                {
                    ShaderArrayExtentKind.Bounded => ShaderArrayExtent.Bounded(value),
                    ShaderArrayExtentKind.Runtime when value == 0 => ShaderArrayExtent.Runtime(),
                    ShaderArrayExtentKind.SpecializationConstant =>
                        ShaderArrayExtent.SpecializationConstant(value),
                    ShaderArrayExtentKind.Runtime => throw new System.Text.Json.JsonException(
                        "Runtime array extents must encode value 0."),
                    _ => throw new System.Text.Json.JsonException($"Unsupported array extent kind {kind}."),
                };
            }

            return new ShaderArrayShape(extents);
        }

        private static ShaderConstantBufferLayoutDocument EncodeConstantBuffer(
            ShaderConstantBufferLayout layout)
        {
            return new ShaderConstantBufferLayoutDocument
            {
                Name = layout.Name,
                ByteSize = layout.ByteSize,
                Variables = Map(layout.Variables, EncodeValueMember),
            };
        }

        private static ShaderConstantBufferLayout DecodeConstantBuffer(
            ShaderConstantBufferLayoutDocument document)
        {
            return new ShaderConstantBufferLayout(
                RequireString(document.Name, "constantBuffer.name"),
                RequireValue(document.ByteSize, "constantBuffer.byteSize"),
                Map(
                    RequireArray(document.Variables, "constantBuffer.variables"),
                    DecodeValueMember));
        }

        private static ShaderValueMemberDocument EncodeValueMember(ShaderValueMember member)
        {
            return new ShaderValueMemberDocument
            {
                Name = member.Name,
                ByteOffset = member.ByteOffset,
                ByteSize = member.ByteSize,
                Value = EncodeValueLayout(member.Value),
            };
        }

        private static ShaderValueMember DecodeValueMember(ShaderValueMemberDocument document)
        {
            return new ShaderValueMember(
                RequireString(document.Name, "valueMember.name"),
                RequireValue(document.ByteOffset, "valueMember.byteOffset"),
                RequireValue(document.ByteSize, "valueMember.byteSize"),
                DecodeValueLayout(RequireObject(document.Value, "valueMember.value")));
        }

        private static ShaderValueLayoutDocument EncodeValueLayout(ShaderValueLayout layout)
        {
            return new ShaderValueLayoutDocument
            {
                Kind = layout.Kind.ToString(),
                ScalarType = layout.ScalarType?.ToString(),
                Rows = layout.Rows,
                Columns = layout.Columns,
                Array = Map(layout.Array.Extents, EncodeArrayExtent),
                ByteSize = layout.ByteSize,
                ProvenArrayStride = layout.ProvenArrayStride,
                ProvenMatrixStride = layout.ProvenMatrixStride,
                MatrixMajorOrder = layout.MatrixMajorOrder?.ToString(),
                Members = Map(layout.Members, EncodeValueMember),
            };
        }

        private static ShaderValueLayout DecodeValueLayout(ShaderValueLayoutDocument document)
        {
            return new ShaderValueLayout(
                ParseEnum<ShaderValueKind>(document.Kind, "valueLayout.kind"),
                ParseNullableEnum<ShaderScalarType>(document.ScalarType, "valueLayout.scalarType"),
                RequireValue(document.Rows, "valueLayout.rows"),
                RequireValue(document.Columns, "valueLayout.columns"),
                DecodeArrayShape(RequireArray(document.Array, "valueLayout.array")),
                document.ByteSize,
                document.ProvenArrayStride,
                document.ProvenMatrixStride,
                ParseNullableEnum<ShaderMatrixMajorOrder>(
                    document.MatrixMajorOrder,
                    "valueLayout.matrixMajorOrder"),
                Map(RequireArray(document.Members, "valueLayout.members"), DecodeValueMember));
        }

        private static ShaderInterfaceVariantDocument EncodeVariant(ShaderInterfaceVariant variant)
        {
            return new ShaderInterfaceVariantDocument
            {
                Key = variant.Key,
                Defines = Copy(variant.Defines),
                Entries = Map(variant.Entries, EncodeEntry),
            };
        }

        private static ShaderInterfaceVariant DecodeVariant(ShaderInterfaceVariantDocument document)
        {
            return new ShaderInterfaceVariant(
                RequireString(document.Key, "variants[].key"),
                RequireArray(document.Defines, "variants[].defines"),
                Map(RequireArray(document.Entries, "variants[].entries"), DecodeEntry));
        }

        private static ShaderInterfaceEntryDocument EncodeEntry(ShaderInterfaceEntry entry)
        {
            return new ShaderInterfaceEntryDocument
            {
                Name = entry.Name,
                Stage = entry.Stage.ToString(),
                LogicalLayoutSignature = entry.LogicalLayoutSignature.ToString(),
                AttachmentInterface = EncodeAttachmentInterface(entry.AttachmentInterface),
                Artifacts = Map(entry.Artifacts, EncodeArtifact),
            };
        }

        private static ShaderInterfaceEntry DecodeEntry(ShaderInterfaceEntryDocument document)
        {
            return new ShaderInterfaceEntry(
                RequireString(document.Name, "entries[].name"),
                ParseEnum<ShaderExecutionStage>(document.Stage, "entries[].stage"),
                ParseSignature(document.LogicalLayoutSignature, "entries[].logicalLayoutSignature"),
                DecodeAttachmentInterface(RequireObject(
                    document.AttachmentInterface,
                    "entries[].attachmentInterface")),
                Map(RequireArray(document.Artifacts, "entries[].artifacts"), DecodeArtifact));
        }

        private static ShaderAttachmentInterfaceDocument EncodeAttachmentInterface(
            ShaderAttachmentInterface attachmentInterface)
        {
            return new ShaderAttachmentInterfaceDocument
            {
                AbiRevision = attachmentInterface.AbiRevision,
                VariantKey = attachmentInterface.VariantKey,
                EntryPoint = attachmentInterface.EntryPoint,
                Stage = attachmentInterface.Stage.ToString(),
                Phase = attachmentInterface.Phase is null
                    ? null
                    : EncodeAttachmentPhase(attachmentInterface.Phase),
            };
        }

        private static ShaderAttachmentInterface DecodeAttachmentInterface(
            ShaderAttachmentInterfaceDocument document)
        {
            return new ShaderAttachmentInterface(
                RequireString(document.VariantKey, "attachmentInterface.variantKey"),
                RequireString(document.EntryPoint, "attachmentInterface.entryPoint"),
                ParseEnum<ShaderExecutionStage>(
                    document.Stage,
                    "attachmentInterface.stage"),
                document.Phase is null
                    ? null
                    : DecodeAttachmentPhase(document.Phase),
                RequireValue(
                    document.AbiRevision,
                    "attachmentInterface.abiRevision"));
        }

        private static ShaderAttachmentPhaseDocument EncodeAttachmentPhase(
            ShaderAttachmentPhase phase)
        {
            return new ShaderAttachmentPhaseDocument
            {
                Phase = phase.Phase,
                DepthStencilAccess = phase.DepthStencilAccess.ToString(),
                DepthExport = phase.DepthExport.ToString(),
                StencilExport = phase.StencilExport.ToString(),
                Attachments = Map(phase.Attachments, EncodeAttachmentDeclaration),
            };
        }

        private static ShaderAttachmentPhase DecodeAttachmentPhase(
            ShaderAttachmentPhaseDocument document)
        {
            return new ShaderAttachmentPhase(
                RequireValue(document.Phase, "attachmentPhase.phase"),
                Map(
                    RequireArray(document.Attachments, "attachmentPhase.attachments"),
                    DecodeAttachmentDeclaration),
                ParseEnum<ShaderDepthStencilAccess>(
                    document.DepthStencilAccess,
                    "attachmentPhase.depthStencilAccess"),
                ParseEnum<ShaderDepthExport>(
                    document.DepthExport,
                    "attachmentPhase.depthExport"),
                ParseEnum<ShaderStencilExport>(
                    document.StencilExport,
                    "attachmentPhase.stencilExport"));
        }

        private static ShaderAttachmentDeclarationDocument EncodeAttachmentDeclaration(
            ShaderAttachmentDeclaration declaration)
        {
            return new ShaderAttachmentDeclarationDocument
            {
                LogicalAttachmentId = declaration.LogicalAttachmentId,
                InputIndex = declaration.InputIndex,
                OutputLocation = declaration.OutputLocation,
                OutputIndex = declaration.OutputIndex,
                OutputComponent = declaration.OutputComponent,
                Aspects = EncodeAttachmentAspects(declaration.Aspect),
                NumericClass = declaration.NumericClass.ToString(),
                SampleMode = declaration.SampleMode.ToString(),
                LayerMode = declaration.LayerMode.ToString(),
                Ordering = declaration.Ordering.ToString(),
                Feedback = declaration.Feedback.ToString(),
                SampledFeedbackBinding = declaration.SampledFeedbackBinding.HasValue
                    ? EncodeBindingKey(declaration.SampledFeedbackBinding.Value)
                    : null,
            };
        }

        private static ShaderAttachmentDeclaration DecodeAttachmentDeclaration(
            ShaderAttachmentDeclarationDocument document)
        {
            return new ShaderAttachmentDeclaration(
                RequireValue(
                    document.LogicalAttachmentId,
                    "attachmentDeclaration.logicalAttachmentId"),
                document.InputIndex,
                document.OutputLocation,
                DecodeAttachmentAspects(RequireArray(
                    document.Aspects,
                    "attachmentDeclaration.aspects")),
                ParseEnum<ShaderAttachmentNumericClass>(
                    document.NumericClass,
                    "attachmentDeclaration.numericClass"),
                ParseEnum<ShaderAttachmentSampleMode>(
                    document.SampleMode,
                    "attachmentDeclaration.sampleMode"),
                ParseEnum<ShaderAttachmentLayerMode>(
                    document.LayerMode,
                    "attachmentDeclaration.layerMode"),
                RequireValue(document.OutputIndex, "attachmentDeclaration.outputIndex"),
                RequireValue(document.OutputComponent, "attachmentDeclaration.outputComponent"),
                ParseEnum<ShaderAttachmentOrdering>(
                    document.Ordering,
                    "attachmentDeclaration.ordering"),
                ParseEnum<ShaderAttachmentFeedback>(
                    document.Feedback,
                    "attachmentDeclaration.feedback"),
                document.SampledFeedbackBinding is null
                    ? null
                    : DecodeBindingKey(document.SampledFeedbackBinding));
        }

        private static ShaderArtifactIdentityDocument EncodeArtifact(ShaderArtifactIdentity artifact)
        {
            return new ShaderArtifactIdentityDocument
            {
                ArtifactKind = artifact.ArtifactKind.ToString(),
                ContentDigest = artifact.ContentDigest,
                ByteLength = artifact.ByteLength,
                ArtifactName = artifact.ArtifactName,
            };
        }

        private static ShaderArtifactIdentity DecodeArtifact(ShaderArtifactIdentityDocument document)
        {
            return new ShaderArtifactIdentity(
                ParseEnum<ShaderArtifactKind>(document.ArtifactKind, "artifacts[].artifactKind"),
                RequireString(document.ContentDigest, "artifacts[].contentDigest"),
                RequireValue(document.ByteLength, "artifacts[].byteLength"),
                document.ArtifactName);
        }

        private static ShaderBackendLayoutsDocument EncodeBackendLayouts(ShaderBackendLayouts layouts)
        {
            return new ShaderBackendLayoutsDocument
            {
                LogicalLayoutSignature = layouts.LogicalLayoutSignature.ToString(),
                Dx12 = layouts.Dx12 is null ? null : new Dx12ShaderBackendLayoutDocument
                {
                    Bindings = Map(layouts.Dx12.Bindings, EncodeDx12Binding),
                },
                Vulkan = layouts.Vulkan is null ? null : new VulkanShaderBackendLayoutDocument
                {
                    Bindings = Map(layouts.Vulkan.Bindings, EncodeVulkanBinding),
                },
                Metal = layouts.Metal is null ? null : new MetalShaderBackendLayoutDocument
                {
                    DirectBindings = Map(layouts.Metal.DirectBindings, EncodeMetalDirectBinding),
                    ReferenceBufferBindings = Map(
                        layouts.Metal.ReferenceBufferBindings,
                        EncodeMetalReferenceBinding),
                },
            };
        }

        private static ShaderBackendLayouts DecodeBackendLayouts(ShaderBackendLayoutsDocument document)
        {
            Dx12ShaderBackendLayout? dx12 = document.Dx12 is null
                ? null
                : new Dx12ShaderBackendLayout(
                    Map(
                        RequireArray(document.Dx12.Bindings, "backendLayouts[].dx12.bindings"),
                        DecodeDx12Binding));
            VulkanShaderBackendLayout? vulkan = document.Vulkan is null
                ? null
                : new VulkanShaderBackendLayout(
                    Map(
                        RequireArray(document.Vulkan.Bindings, "backendLayouts[].vulkan.bindings"),
                        DecodeVulkanBinding));
            MetalShaderBackendLayout? metal = document.Metal is null
                ? null
                : new MetalShaderBackendLayout(
                    Map(
                        RequireArray(
                            document.Metal.DirectBindings,
                            "backendLayouts[].metal.directBindings"),
                        DecodeMetalDirectBinding),
                    Map(
                        RequireArray(
                            document.Metal.ReferenceBufferBindings,
                            "backendLayouts[].metal.referenceBufferBindings"),
                        DecodeMetalReferenceBinding));

            return new ShaderBackendLayouts(
                ParseSignature(
                    document.LogicalLayoutSignature,
                    "backendLayouts[].logicalLayoutSignature"),
                dx12,
                vulkan,
                metal);
        }

        private static Dx12ShaderBindingMappingDocument EncodeDx12Binding(
            Dx12ShaderBindingMapping mapping)
        {
            return new Dx12ShaderBindingMappingDocument
            {
                LogicalBinding = EncodeBindingKey(mapping.LogicalBinding),
                RegisterSpace = mapping.RegisterSpace,
                ShaderRegister = mapping.ShaderRegister,
                RegisterClass = mapping.RegisterClass.ToString(),
            };
        }

        private static Dx12ShaderBindingMapping DecodeDx12Binding(
            Dx12ShaderBindingMappingDocument document)
        {
            return new Dx12ShaderBindingMapping(
                DecodeBindingKey(RequireObject(document.LogicalBinding, "dx12.bindings[].logicalBinding")),
                RequireValue(document.RegisterSpace, "dx12.bindings[].registerSpace"),
                RequireValue(document.ShaderRegister, "dx12.bindings[].shaderRegister"),
                ParseEnum<ShaderBindingClass>(
                    document.RegisterClass,
                    "dx12.bindings[].registerClass"));
        }

        private static VulkanShaderBindingMappingDocument EncodeVulkanBinding(
            VulkanShaderBindingMapping mapping)
        {
            return new VulkanShaderBindingMappingDocument
            {
                LogicalBinding = EncodeBindingKey(mapping.LogicalBinding),
                DescriptorSet = mapping.DescriptorSet,
                Binding = mapping.Binding,
                DescriptorKind = mapping.DescriptorKind.ToString(),
            };
        }

        private static VulkanShaderBindingMapping DecodeVulkanBinding(
            VulkanShaderBindingMappingDocument document)
        {
            return new VulkanShaderBindingMapping(
                DecodeBindingKey(RequireObject(document.LogicalBinding, "vulkan.bindings[].logicalBinding")),
                RequireValue(document.DescriptorSet, "vulkan.bindings[].descriptorSet"),
                RequireValue(document.Binding, "vulkan.bindings[].binding"),
                ParseEnum<VulkanDescriptorKind>(
                    document.DescriptorKind,
                    "vulkan.bindings[].descriptorKind"));
        }

        private static MetalDirectBindingMappingDocument EncodeMetalDirectBinding(
            MetalDirectBindingMapping mapping)
        {
            return new MetalDirectBindingMappingDocument
            {
                LogicalBinding = EncodeBindingKey(mapping.LogicalBinding),
                ArgumentTable = mapping.ArgumentTable,
                Namespace = mapping.Namespace.ToString(),
                Index = mapping.Index,
            };
        }

        private static MetalDirectBindingMapping DecodeMetalDirectBinding(
            MetalDirectBindingMappingDocument document)
        {
            return new MetalDirectBindingMapping(
                DecodeBindingKey(RequireObject(document.LogicalBinding, "metal.directBindings[].logicalBinding")),
                RequireValue(document.ArgumentTable, "metal.directBindings[].argumentTable"),
                ParseEnum<ShaderPhysicalBindingNamespace>(
                    document.Namespace,
                    "metal.directBindings[].namespace"),
                RequireValue(document.Index, "metal.directBindings[].index"));
        }

        private static MetalReferenceBufferBindingMappingDocument EncodeMetalReferenceBinding(
            MetalReferenceBufferBindingMapping mapping)
        {
            return new MetalReferenceBufferBindingMappingDocument
            {
                LogicalBinding = EncodeBindingKey(mapping.LogicalBinding),
                ArgumentTable = mapping.ArgumentTable,
                ResourceNamespace = mapping.ResourceNamespace.ToString(),
                ReferenceBufferIndex = mapping.ReferenceBufferIndex,
                ByteOffset = mapping.ByteOffset,
                ReferenceCount = mapping.ReferenceCount,
            };
        }

        private static MetalReferenceBufferBindingMapping DecodeMetalReferenceBinding(
            MetalReferenceBufferBindingMappingDocument document)
        {
            return new MetalReferenceBufferBindingMapping(
                DecodeBindingKey(RequireObject(
                    document.LogicalBinding,
                    "metal.referenceBufferBindings[].logicalBinding")),
                RequireValue(document.ArgumentTable, "metal.referenceBufferBindings[].argumentTable"),
                ParseEnum<ShaderPhysicalBindingNamespace>(
                    document.ResourceNamespace,
                    "metal.referenceBufferBindings[].resourceNamespace"),
                RequireValue(
                    document.ReferenceBufferIndex,
                    "metal.referenceBufferBindings[].referenceBufferIndex"),
                RequireValue(document.ByteOffset, "metal.referenceBufferBindings[].byteOffset"),
                RequireValue(document.ReferenceCount, "metal.referenceBufferBindings[].referenceCount"));
        }

        private static string[] EncodeTargets(ShaderProgramTarget targets)
        {
            List<string> result = new();
            foreach (ShaderProgramTarget target in new[]
                     {
                         ShaderProgramTarget.DirectX12,
                         ShaderProgramTarget.Vulkan,
                         ShaderProgramTarget.MetalMsl,
                     })
            {
                if ((targets & target) != 0)
                {
                    result.Add(target.ToString());
                }
            }
            return result.ToArray();
        }

        private static ShaderProgramTarget DecodeTargets(string[] values)
        {
            if (values.Length == 0)
            {
                throw new System.Text.Json.JsonException(
                    "Manifest target arrays must not be empty.");
            }

            HashSet<ShaderProgramTarget> seen = new();
            ShaderProgramTarget result = ShaderProgramTarget.None;
            foreach (string value in values)
            {
                ShaderProgramTarget target = ParseEnum<ShaderProgramTarget>(
                    value,
                    "targets[]");
                if (target is ShaderProgramTarget.None or ShaderProgramTarget.All
                    || !seen.Add(target))
                {
                    throw new System.Text.Json.JsonException(
                        $"Manifest target {target} is empty, composite, or duplicated.");
                }
                result |= target;
            }
            return result;
        }

        private static string[] EncodeStages(ShaderStageMask mask)
        {
            List<string> stages = new();
            foreach (ShaderExecutionStage stage in Enum.GetValues<ShaderExecutionStage>())
            {
                ShaderStageMask stageMask = ShaderStageMaskUtility.FromStage(stage);
                if ((mask & stageMask) != 0)
                {
                    stages.Add(stage.ToString());
                }
            }

            return stages.ToArray();
        }

        private static ShaderStageMask DecodeStages(string[] values)
        {
            if (values.Length == 0)
            {
                throw new System.Text.Json.JsonException("Binding stage arrays must not be empty.");
            }

            HashSet<ShaderExecutionStage> seen = new();
            ShaderStageMask result = ShaderStageMask.None;
            foreach (string value in values)
            {
                ShaderExecutionStage stage = ParseEnum<ShaderExecutionStage>(value, "stages[]");
                if (!seen.Add(stage))
                {
                    throw new System.Text.Json.JsonException($"Binding stage {stage} is duplicated.");
                }

                result |= ShaderStageMaskUtility.FromStage(stage);
            }

            ShaderStageMaskUtility.Validate(result);
            return result;
        }

        private static ShaderLayoutSignature ParseSignature(string? value, string path)
        {
            string text = RequireString(value, path);
            if (!ShaderLayoutSignature.TryParse(text, out ShaderLayoutSignature signature))
            {
                throw new System.Text.Json.JsonException(
                    $"{path} must contain a 64-character lowercase SHA-256 signature.");
            }

            return signature;
        }

        private static string[] EncodeAttachmentAspects(ShaderAttachmentAspect aspects)
        {
            List<string> result = new();
            foreach (ShaderAttachmentAspect aspect in new[]
                     {
                         ShaderAttachmentAspect.Color,
                         ShaderAttachmentAspect.Depth,
                         ShaderAttachmentAspect.Stencil,
                     })
            {
                if ((aspects & aspect) != 0)
                {
                    result.Add(aspect.ToString());
                }
            }

            return result.ToArray();
        }

        private static ShaderAttachmentAspect DecodeAttachmentAspects(
            string[] values)
        {
            if (values.Length == 0)
            {
                throw new System.Text.Json.JsonException(
                    "Attachment aspect arrays must not be empty.");
            }

            ShaderAttachmentAspect result = ShaderAttachmentAspect.None;
            HashSet<ShaderAttachmentAspect> seen = new();
            foreach (string value in values)
            {
                ShaderAttachmentAspect aspect =
                    ParseEnum<ShaderAttachmentAspect>(value, "attachmentDeclaration.aspects[]");
                if (aspect == ShaderAttachmentAspect.None
                    || !seen.Add(aspect))
                {
                    throw new System.Text.Json.JsonException(
                        $"Attachment aspect {aspect} is empty or duplicated.");
                }

                result |= aspect;
            }

            return result;
        }

        private static TEnum ParseEnum<TEnum>(string? value, string path)
            where TEnum : struct, Enum
        {
            string text = RequireString(value, path);
            if (!Enum.TryParse(text, ignoreCase: false, out TEnum result) || !Enum.IsDefined(result))
            {
                throw new System.Text.Json.JsonException(
                    $"{path} contains unsupported {typeof(TEnum).Name} value '{text}'.");
            }

            return result;
        }

        private static TEnum? ParseNullableEnum<TEnum>(string? value, string path)
            where TEnum : struct, Enum
        {
            return value is null ? null : ParseEnum<TEnum>(value, path);
        }

        private static string RequireString(string? value, string path)
        {
            if (value is null)
            {
                throw new System.Text.Json.JsonException($"Required property {path} is missing.");
            }

            return value;
        }

        private static T RequireObject<T>(T? value, string path)
            where T : class
        {
            return value ?? throw new System.Text.Json.JsonException(
                $"Required property {path} is missing.");
        }

        private static T[] RequireArray<T>(T[]? value, string path)
        {
            return value ?? throw new System.Text.Json.JsonException(
                $"Required property {path} is missing.");
        }

        private static T RequireValue<T>(T? value, string path)
            where T : struct
        {
            return value ?? throw new System.Text.Json.JsonException(
                $"Required property {path} is missing.");
        }

        private static TOutput[] Map<TInput, TOutput>(
            IReadOnlyList<TInput> values,
            Func<TInput, TOutput> converter)
        {
            TOutput[] result = new TOutput[values.Count];
            for (int index = 0; index < values.Count; ++index)
            {
                result[index] = converter(values[index]);
            }

            return result;
        }

        private static string[] Copy(IReadOnlyList<string> values)
        {
            string[] result = new string[values.Count];
            for (int index = 0; index < values.Count; ++index)
            {
                result[index] = values[index];
            }

            return result;
        }
    }
}
