using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SharpShader.HLSLCrossCompiler;
using SharpShader.HLSLCrossCompiler.Internal;

namespace SharpShader.Compilation.Internal
{
    internal static partial class ShaderProgramCompilationPipeline
    {

        private static ShaderInterfaceLayout BuildUnionLayout(
            IReadOnlyList<EntryState> entries)
        {
            Dictionary<ShaderBindingKey, BindingAccumulator> bindings = new();
            foreach (EntryState entry in entries)
            {
                foreach (ShaderResourceBindingReflection resource in entry.DxilEntry.Resources)
                {
                    if (!bindings.TryGetValue(
                            resource.Key,
                            out BindingAccumulator? accumulator))
                    {
                        accumulator = new BindingAccumulator(resource.LogicalBinding);
                        bindings.Add(resource.Key, accumulator);
                    }
                    else
                    {
                        accumulator.Merge(
                            resource.LogicalBinding,
                            $"entry {entry.Definition.Name} ({entry.Definition.Stage})");
                    }
                }
            }

            return BuildLayout(bindings.Values);
        }

        private static ShaderInterfaceLayout BuildLayout(
            IEnumerable<BindingAccumulator> accumulators)
        {
            BindingAccumulator[] ordered = accumulators.ToArray();
            Array.Sort(ordered, static (left, right) =>
                CompareKeys(left.Key, right.Key));

            Dictionary<string, ShaderBindingKey> names = new(StringComparer.Ordinal);
            ShaderLogicalBinding[] bindings = new ShaderLogicalBinding[ordered.Length];
            for (int index = 0; index < ordered.Length; ++index)
            {
                bindings[index] = ordered[index].Build();
                AddBindingName(names, bindings[index].CanonicalName, bindings[index].Key);
                foreach (string alias in bindings[index].Aliases)
                {
                    AddBindingName(names, alias, bindings[index].Key);
                }
            }

            return new ShaderInterfaceLayout(bindings);
        }

        private static void AddBindingName(
            Dictionary<string, ShaderBindingKey> names,
            string name,
            ShaderBindingKey key)
        {
            if (names.TryGetValue(name, out ShaderBindingKey existing)
                && existing != key)
            {
                throw Failure(
                    $"Reflected resource name {name} identifies both {existing} and {key}. "
                    + "Logical binding names and aliases must be globally unambiguous.");
            }

            names[name] = key;
        }

        private static void InternLayouts(List<VariantState> variants)
        {
            Dictionary<ShaderLayoutSignature, List<VariantState>> groups = new();
            foreach (VariantState variant in variants)
            {
                if (!groups.TryGetValue(
                        variant.InitialLayout.Signature,
                        out List<VariantState>? group))
                {
                    group = new List<VariantState>();
                    groups.Add(variant.InitialLayout.Signature, group);
                }

                group.Add(variant);
            }

            foreach ((ShaderLayoutSignature signature, List<VariantState> group) in groups)
            {
                ShaderInterfaceLayout first = group[0].InitialLayout;
                Dictionary<ShaderBindingKey, BindingAccumulator> merged = new();
                foreach (VariantState variant in group)
                {
                    if (!first.AbiEquals(variant.InitialLayout))
                    {
                        throw new InvalidOperationException(
                            $"Shader layout signature collision detected for {signature}; "
                            + "structural equality rejected interning.");
                    }

                    foreach (ShaderLogicalBinding binding in variant.InitialLayout.Bindings)
                    {
                        if (!merged.TryGetValue(
                                binding.Key,
                                out BindingAccumulator? accumulator))
                        {
                            accumulator = new BindingAccumulator(binding);
                            merged.Add(binding.Key, accumulator);
                        }
                        else
                        {
                            accumulator.Merge(
                                binding,
                                $"variant {variant.Definition.Key}");
                        }
                    }
                }

                ShaderInterfaceLayout interned = BuildLayout(merged.Values);
                if (interned.Signature != signature || !first.AbiEquals(interned))
                {
                    throw new InvalidOperationException(
                        $"Interning reflection metadata changed logical shader ABI {signature}.");
                }

                foreach (VariantState variant in group)
                {
                    variant.Layout = interned;
                }
            }
        }

        private static void ValidateMetalCapacityCoverage(
            ShaderProgramCompileRequest request,
            IReadOnlyList<VariantState> variants)
        {
            if (request.MetalArrayCapacities.Count == 0)
            {
                return;
            }

            if ((request.Targets & ShaderProgramTarget.MetalMsl) == 0)
            {
                throw InvalidRequest(
                    "Metal array capacities were provided, but MetalMsl is not a requested target.");
            }

            HashSet<ShaderBindingKey> knownBindings = new();
            foreach (VariantState variant in variants)
            {
                foreach (ShaderLogicalBinding binding in variant.Layout.Bindings)
                {
                    knownBindings.Add(binding.Key);
                }
            }

            foreach (ShaderBindingKey key in request.MetalArrayCapacities.Keys)
            {
                if (!knownBindings.Contains(key))
                {
                    throw InvalidRequest(
                        $"Metal array capacity references unknown logical binding {key}.");
                }
            }
        }

        private static void PlanVariant(
            ShaderProgramCompileRequest request,
            VariantState variant)
        {
            Dictionary<ShaderBindingKey, uint> capacities = new();
            HashSet<ShaderBindingKey> layoutKeys = new(
                variant.Layout.Bindings.Select(static binding => binding.Key));
            foreach ((ShaderBindingKey key, uint capacity) in request.MetalArrayCapacities)
            {
                if (layoutKeys.Contains(key))
                {
                    capacities.Add(key, capacity);
                }
            }

            variant.BackendLayouts = ShaderBackendLayoutPlanner.Plan(
                variant.Layout,
                capacities.Count == 0 ? null : capacities);
            RebuildLogicalReflections(variant);
        }

        private static void RebuildLogicalReflections(VariantState variant)
        {
            Dictionary<ShaderBindingKey, ShaderLogicalBinding> bindingsByKey =
                variant.Layout.Bindings.ToDictionary(
                    static binding => binding.Key,
                    static binding => binding);

            foreach (CompileUnitState unit in variant.Units)
            {
                List<ShaderEntryPointReflection> rebuiltEntries = new(unit.Entries.Count);
                foreach (EntryState entry in unit.Entries)
                {
                    List<ShaderResourceBindingReflection> resources =
                        new(entry.DxilEntry.Resources.Count);
                    foreach (ShaderResourceBindingReflection original in entry.DxilEntry.Resources)
                    {
                        ShaderLogicalBinding binding = bindingsByKey[original.Key];
                        resources.Add(new ShaderResourceBindingReflection(
                            binding,
                            CreateDx12PhysicalLocation(binding.Key)));
                    }

                    ShaderEntryPointReflection rebuilt = new(
                        entry.Definition.Name,
                        entry.Definition.Stage,
                        resources,
                        entry.DxilEntry.ThreadGroupSize,
                        entry.DxilEntry.StageInputs,
                        entry.DxilEntry.StageOutputs,
                        entry.DxilEntry.InputAttachments,
                        entry.DxilEntry.AttachmentRequirements);
                    entry.LogicalEntry = rebuilt;
                    rebuiltEntries.Add(rebuilt);
                }

                unit.LogicalReflection = new ShaderArtifactReflection(
                    ShaderArtifactKind.Dxil,
                    rebuiltEntries);
            }
        }
}
}
