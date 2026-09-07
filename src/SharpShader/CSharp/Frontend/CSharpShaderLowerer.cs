using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SharpShader.CSharp.ShaderLib;

namespace SharpShader.CSharp.Frontend
{
    internal sealed class CSharpShaderLowerer
    {
        private readonly Compilation m_Compilation;
        private readonly CancellationToken m_CancellationToken;
        private readonly List<CSharpShaderDiagnostic> m_Diagnostics = new List<CSharpShaderDiagnostic>();
        private readonly Dictionary<IMethodSymbol, MethodModel> m_Methods =
            new Dictionary<IMethodSymbol, MethodModel>(SymbolEqualityComparer.Default);
        private readonly Dictionary<INamedTypeSymbol, StructModel> m_Structs =
            new Dictionary<INamedTypeSymbol, StructModel>(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, ResourceModel> m_Resources =
            new Dictionary<ISymbol, ResourceModel>(SymbolEqualityComparer.Default);
        private readonly HashSet<IMethodSymbol> m_Visiting =
            new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        private bool m_UsesRayQuery;
        private bool m_UsesWaveOperations;
        private readonly bool m_IncludeHostDiagnostics;

        private CSharpShaderLowerer(Compilation compilation, bool includeHostDiagnostics, CancellationToken cancellationToken)
        {
            m_Compilation = compilation;
            m_CancellationToken = cancellationToken;
            m_IncludeHostDiagnostics = includeHostDiagnostics;
        }

        internal static CSharpShaderTranslation Run(
            Compilation compilation,
            bool includeHostDiagnostics,
            CancellationToken cancellationToken)
        {
            return new CSharpShaderLowerer(compilation, includeHostDiagnostics, cancellationToken).Translate();
        }

        private CSharpShaderTranslation Translate()
        {
            List<EntryModel> entries = DiscoverEntries();
            if (m_IncludeHostDiagnostics)
            {
                CollectCompilationDiagnostics(entries);
            }
            if (entries.Count == 0 && !HasErrors())
            {
                m_Diagnostics.Add(new CSharpShaderDiagnostic(
                    CSharpShaderDiagnosticIds.NoEntry,
                    CSharpShaderDiagnosticSeverity.Error,
                    "No SharpSL entry point was found. Mark a static method with [ComputeShader], [VertexShader], [FragmentShader], or [NumThreads]."));
            }

            if (!HasErrors())
            {
                for (int index = 0; index < entries.Count; ++index)
                {
                    CollectMethod(entries[index].Method, entries[index].Method.DeclaringSyntaxReferences);
                }
            }

            if (!m_IncludeHostDiagnostics)
            {
                CollectCompilationDiagnostics(entries);
            }

            string hlsl = HasErrors() ? string.Empty : EmitHlsl(entries);
            string dump = BuildDump(entries);
            List<CSharpShaderEntryTranslation> entryTranslations = new List<CSharpShaderEntryTranslation>();
            for (int index = 0; index < entries.Count; ++index)
            {
                EntryModel entry = entries[index];
                entryTranslations.Add(new CSharpShaderEntryTranslation(
                    entry.HlslName,
                    entry.Stage,
                    entry.ThreadGroup,
                    entry.ColorTargetCount,
                    m_UsesRayQuery && entry.Stage == CSharpShaderStage.Compute,
                    m_UsesWaveOperations));
            }

            List<CSharpShaderResourceBinding> resources = new List<CSharpShaderResourceBinding>();
            foreach (ResourceModel resource in m_Resources.Values)
            {
                resources.Add(new CSharpShaderResourceBinding(
                    resource.Name,
                    resource.Kind,
                    resource.HlslType,
                    resource.Register,
                    resource.Space,
                    resource.PushConstant,
                    resource.GroupShared));
            }

            resources.Sort(static (left, right) =>
            {
                int space = left.Space.CompareTo(right.Space);
                if (space != 0)
                {
                    return space;
                }

                int register = left.Register.CompareTo(right.Register);
                return register != 0
                    ? register
                    : string.CompareOrdinal(left.Name, right.Name);
            });

            return new CSharpShaderTranslation(
                hlsl,
                dump,
                entryTranslations,
                resources,
                m_Diagnostics);
        }

        private void CollectCompilationDiagnostics(List<EntryModel> entries)
        {
            foreach (Diagnostic diagnostic in m_Compilation.GetDiagnostics(m_CancellationToken))
            {
                if (diagnostic.Severity != DiagnosticSeverity.Error
                    || (!m_IncludeHostDiagnostics && !IsShaderDiagnostic(diagnostic.Location, entries)))
                {
                    continue;
                }

                m_Diagnostics.Add(CSharpShaderDiagnostic.FromLocation(
                    CSharpShaderDiagnosticIds.CSharpError,
                    CSharpShaderDiagnosticSeverity.Error,
                    diagnostic.GetMessage(),
                    diagnostic.Location));
            }
        }

        private bool IsShaderDiagnostic(Location location, List<EntryModel> entries)
        {
            if (!location.IsInSource)
            {
                return true;
            }
            foreach (EntryModel entry in entries)
            {
                if (ContainsDiagnostic(entry.Method, location))
                {
                    return true;
                }
            }
            foreach (IMethodSymbol method in m_Methods.Keys)
            {
                if (ContainsDiagnostic(method, location))
                {
                    return true;
                }
            }
            foreach (INamedTypeSymbol type in m_Structs.Keys)
            {
                if (ContainsDiagnostic(type, location))
                {
                    return true;
                }
            }
            foreach (ISymbol resource in m_Resources.Keys)
            {
                if (ContainsDiagnostic(resource, location))
                {
                    return true;
                }
            }
            return false;
        }

        private bool ContainsDiagnostic(ISymbol symbol, Location location)
        {
            foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
            {
                if (reference.SyntaxTree == location.SourceTree
                    && reference.GetSyntax(m_CancellationToken).FullSpan.IntersectsWith(location.SourceSpan))
                {
                    return true;
                }
            }
            return false;
        }

        private List<EntryModel> DiscoverEntries()
        {
            List<EntryModel> entries = new List<EntryModel>();
            foreach (SyntaxTree tree in m_Compilation.SyntaxTrees)
            {
                m_CancellationToken.ThrowIfCancellationRequested();
                SemanticModel model = m_Compilation.GetSemanticModel(tree);
                foreach (MethodDeclarationSyntax method in tree.GetRoot(m_CancellationToken)
                             .DescendantNodes()
                             .OfTypeSafe<MethodDeclarationSyntax>())
                {
                    IMethodSymbol? symbol = model.GetDeclaredSymbol(method, m_CancellationToken);
                    if (symbol is null || !symbol.IsStatic)
                    {
                        continue;
                    }

                    if (!TryReadEntry(symbol, out EntryModel entry))
                    {
                        continue;
                    }

                    entries.Add(entry);
                }
            }

            entries.Sort(static (left, right) =>
            {
                int stage = left.Stage.CompareTo(right.Stage);
                return stage != 0
                    ? stage
                    : string.CompareOrdinal(left.HlslName, right.HlslName);
            });
            return entries;
        }

        private bool TryReadEntry(IMethodSymbol symbol, out EntryModel entry)
        {
            entry = default;
            CSharpShaderStage? stage = null;
            string? hlslName = null;
            CSharpShaderThreadGroup? threads = null;
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                string? name = attribute.AttributeClass?.ToDisplayString();
                if (name == CSharpShaderNameMap.ShaderLibNamespace + ".ComputeShaderAttribute")
                {
                    stage = CSharpShaderStage.Compute;
                    hlslName = ReadStringArgument(attribute, symbol.Name);
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".VertexShaderAttribute")
                {
                    stage = CSharpShaderStage.Vertex;
                    hlslName = ReadStringArgument(attribute, symbol.Name);
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".FragmentShaderAttribute")
                {
                    stage = CSharpShaderStage.Fragment;
                    hlslName = ReadStringArgument(attribute, symbol.Name);
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".NumThreadsAttribute")
                {
                    if (attribute.ConstructorArguments.Length == 3)
                    {
                        threads = new CSharpShaderThreadGroup(
                            Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture),
                            Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture),
                            Convert.ToInt32(attribute.ConstructorArguments[2].Value, CultureInfo.InvariantCulture));
                    }

                    stage ??= CSharpShaderStage.Compute;
                    hlslName ??= symbol.Name;
                }
            }

            if (stage is null || hlslName is null)
            {
                return false;
            }

            if (stage == CSharpShaderStage.Compute && threads is null)
            {
                threads = new CSharpShaderThreadGroup(1, 1, 1);
            }

            int colorTargets = 0;
            foreach (IParameterSymbol parameter in symbol.Parameters)
            {
                foreach (AttributeData attribute in parameter.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString()
                        == CSharpShaderNameMap.ShaderLibNamespace + ".SV.TargetAttribute")
                    {
                        int index = attribute.ConstructorArguments.Length == 1
                            ? Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture)
                            : 0;
                        colorTargets = Math.Max(colorTargets, index + 1);
                    }
                }
            }

            entry = new EntryModel(symbol, hlslName, stage.Value, threads, colorTargets);
            return true;
        }

        private static string ReadStringArgument(AttributeData attribute, string fallback)
        {
            if (attribute.ConstructorArguments.Length == 1
                && attribute.ConstructorArguments[0].Value is string value
                && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            return fallback;
        }

        private void CollectMethod(IMethodSymbol method, IEnumerable<SyntaxReference> syntaxReferences)
        {
            m_CancellationToken.ThrowIfCancellationRequested();
            method = method.OriginalDefinition;
            if (m_Methods.ContainsKey(method))
            {
                return;
            }

            if (!m_Visiting.Add(method))
            {
                AddError(
                    CSharpShaderDiagnosticIds.Recursion,
                    "Recursive shader functions are not supported.",
                    method.Locations.Length > 0 ? method.Locations[0] : Location.None);
                return;
            }

            MethodDeclarationSyntax? syntax = null;
            LocalFunctionStatementSyntax? localSyntax = null;
            SemanticModel? model = null;
            foreach (SyntaxReference reference in syntaxReferences)
            {
                SyntaxNode node = reference.GetSyntax(m_CancellationToken);
                model = m_Compilation.GetSemanticModel(node.SyntaxTree);
                syntax = node as MethodDeclarationSyntax;
                localSyntax = node as LocalFunctionStatementSyntax;
                if (syntax is not null || localSyntax is not null)
                {
                    break;
                }
            }

            BlockSyntax? body = syntax?.Body ?? localSyntax?.Body;
            if (body is null)
            {
                AddError(
                    CSharpShaderDiagnosticIds.UnsupportedSyntax,
                    "Shader functions must have a block body.",
                    method.Locations.Length > 0 ? method.Locations[0] : Location.None);
                m_Visiting.Remove(method);
                return;
            }

            if (model is null)
            {
                m_Visiting.Remove(method);
                return;
            }

            List<ISymbol> captures = new List<ISymbol>();
            if (localSyntax is not null)
            {
                CollectCaptures(model, localSyntax, method, captures);
            }

            CollectInvocations(model, body, method);
            CollectResources(model, body);
            CollectType(method.ReturnType, method.Locations.Length > 0 ? method.Locations[0] : Location.None);
            foreach (IParameterSymbol parameter in method.Parameters)
            {
                CollectType(parameter.Type, parameter.Locations.Length > 0 ? parameter.Locations[0] : Location.None);
            }

            m_Methods[method] = new MethodModel(method, syntax, localSyntax, model, captures);
            m_Visiting.Remove(method);
        }

        private void CollectInvocations(SemanticModel model, SyntaxNode body, IMethodSymbol owner)
        {
            foreach (SyntaxNode node in body.DescendantNodes())
            {
                if (node is InvocationExpressionSyntax invocation)
                {
                    IMethodSymbol? callee = model.GetSymbolInfo(invocation, m_CancellationToken).Symbol as IMethodSymbol;
                    if (callee is null)
                    {
                        continue;
                    }

                    if (IsIntrinsic(callee)
                        || IsMathFunction(callee)
                        || callee.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
                    {
                        if (IsRayQuery(callee.ContainingType))
                        {
                            m_UsesRayQuery = true;
                        }

                        continue;
                    }

                    if (callee.MethodKind == MethodKind.LocalFunction
                        || (callee.IsStatic && callee.ContainingType is not null
                            && !IsFrameworkType(callee.ContainingType)))
                    {
                        CollectMethod(callee, callee.DeclaringSyntaxReferences);
                    }
                    else if (!IsFrameworkType(callee.ContainingType))
                    {
                        AddError(
                            CSharpShaderDiagnosticIds.UnsupportedSyntax,
                            "Unsupported call '" + callee.Name + "'.",
                            invocation.GetLocation());
                    }
                }
                else if (node is ObjectCreationExpressionSyntax
                         or ImplicitObjectCreationExpressionSyntax
                         or TryStatementSyntax
                         or ThrowStatementSyntax
                         or AwaitExpressionSyntax
                         or QueryExpressionSyntax
                         or AnonymousMethodExpressionSyntax
                         or LambdaExpressionSyntax
                         or InterpolatedStringExpressionSyntax)
                {
                    if (node is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
                    {
                        ITypeSymbol? created = model.GetTypeInfo(node, m_CancellationToken).Type;
                        if (created is { TypeKind: TypeKind.Class })
                        {
                            AddError(
                                CSharpShaderDiagnosticIds.UnsupportedSyntax,
                                "Reference-type allocation is not supported in SharpSL.",
                                node.GetLocation());
                        }
                        else if (created is not null)
                        {
                            CollectType(created, node.GetLocation());
                        }

                        continue;
                    }

                    AddError(
                        CSharpShaderDiagnosticIds.UnsupportedSyntax,
                        "Unsupported C# construct '" + node.Kind() + "'.",
                        node.GetLocation());
                }
            }

            _ = owner;
        }

        private void CollectResources(SemanticModel model, SyntaxNode body)
        {
            foreach (IdentifierNameSyntax identifier in body.DescendantNodes().OfTypeSafe<IdentifierNameSyntax>())
            {
                ISymbol? symbol = model.GetSymbolInfo(identifier, m_CancellationToken).Symbol;
                if (symbol is IFieldSymbol field && field.IsStatic)
                {
                    TryAddResource(field);
                }
            }
        }

        private void TryAddResource(IFieldSymbol field)
        {
            if (m_Resources.ContainsKey(field))
            {
                return;
            }

            if (!TryMapResource(field.Type, out CSharpShaderResourceKind kind, out string hlslType))
            {
                return;
            }

            int register = 0;
            int space = 0;
            bool push = false;
            bool groupShared = false;
            bool hasBinding = false;
            foreach (AttributeData attribute in field.GetAttributes())
            {
                string? name = attribute.AttributeClass?.ToDisplayString();
                if (name == CSharpShaderNameMap.ShaderLibNamespace + ".BindingAttribute"
                    && attribute.ConstructorArguments.Length == 2)
                {
                    register = Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture);
                    space = Convert.ToInt32(attribute.ConstructorArguments[1].Value, CultureInfo.InvariantCulture);
                    hasBinding = true;
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".PushConstantAttribute")
                {
                    push = true;
                    hasBinding = true;
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".GroupSharedAttribute")
                {
                    groupShared = true;
                    hasBinding = true;
                }
            }

            if (!hasBinding)
            {
                AddError(
                    CSharpShaderDiagnosticIds.InvalidResource,
                    "Resource '" + field.Name + "' requires [Binding], [PushConstant], or [GroupShared].",
                    field.Locations.Length > 0 ? field.Locations[0] : Location.None);
                return;
            }

            if (field.Type is INamedTypeSymbol named && named.TypeArguments.Length == 1)
            {
                CollectType(
                    named.TypeArguments[0],
                    field.Locations.Length > 0 ? field.Locations[0] : Location.None);
            }

            m_Resources[field] = new ResourceModel(
                field.Name,
                kind,
                hlslType,
                register,
                space,
                push,
                groupShared);
        }

        private void CollectType(ITypeSymbol type, Location location)
        {
            type = Unwrap(type);
            if (type.SpecialType != SpecialType.None
                || TryMapKnownType(type, out _)
                || type.TypeKind == TypeKind.Enum
                || type.SpecialType == SpecialType.System_Void)
            {
                if (type.SpecialType == SpecialType.System_String)
                {
                    AddError(
                        CSharpShaderDiagnosticIds.UnknownType,
                        "System.String is not a GPU type.",
                        location);
                }

                return;
            }

            if (type is not INamedTypeSymbol named || named.TypeKind != TypeKind.Struct)
            {
                if (!IsResourceType(type) && !IsRayQuery(type) && type.Name != "RayDesc")
                {
                    AddError(
                        CSharpShaderDiagnosticIds.UnknownType,
                        "Unsupported shader type '" + type.ToDisplayString() + "'.",
                        location);
                }

                return;
            }

            if (m_Structs.ContainsKey(named) || IsFrameworkType(named))
            {
                return;
            }

            bool stageInOut = false;
            GpuLayoutKind? layoutKind = null;
            foreach (AttributeData attribute in named.GetAttributes())
            {
                string? name = attribute.AttributeClass?.ToDisplayString();
                if (name == CSharpShaderNameMap.ShaderLibNamespace + ".StageInOutAttribute")
                {
                    stageInOut = true;
                }
                else if (name == CSharpShaderNameMap.ShaderLibNamespace + ".GpuLayoutAttribute")
                {
                    layoutKind = attribute.ConstructorArguments.Length == 1
                        ? (GpuLayoutKind)Convert.ToInt32(
                            attribute.ConstructorArguments[0].Value,
                            CultureInfo.InvariantCulture)
                        : GpuLayoutKind.ConstantBuffer;
                }
            }

            List<FieldModel> fields = new List<FieldModel>();
            foreach (IFieldSymbol field in named.GetMembers().OfTypeSafe<IFieldSymbol>())
            {
                if (field.IsStatic)
                {
                    continue;
                }

                CollectType(field.Type, field.Locations.Length > 0 ? field.Locations[0] : location);
                if (!TryMapKnownType(Unwrap(field.Type), out string hlslType)
                    && Unwrap(field.Type) is INamedTypeSymbol fieldStruct
                    && m_Structs.ContainsKey(fieldStruct))
                {
                    hlslType = fieldStruct.Name;
                }
                else if (!TryMapKnownType(Unwrap(field.Type), out hlslType)
                         && Unwrap(field.Type).TypeKind == TypeKind.Enum)
                {
                    hlslType = "int";
                }

                if (string.IsNullOrEmpty(hlslType))
                {
                    continue;
                }

                fields.Add(new FieldModel(field.Name, hlslType, field.Type));
            }

            if (layoutKind is not null)
            {
                ValidateGpuLayout(named, fields, layoutKind.Value, location);
            }

            m_Structs[named] = new StructModel(named.Name, fields, stageInOut);
        }

        private void ValidateGpuLayout(
            INamedTypeSymbol type,
            IReadOnlyList<FieldModel> fields,
            GpuLayoutKind kind,
            Location location)
        {
            List<KeyValuePair<string, GpuTypeShape>> gpuFields = new List<KeyValuePair<string, GpuTypeShape>>();
            int hostOffset = 0;
            int hostAlign = 1;
            for (int index = 0; index < fields.Count; ++index)
            {
                FieldModel field = fields[index];
                string metadata = Unwrap(field.ClrType).ToDisplayString();
                GpuTypeShape? gpuType = TryGpuShape(field.ClrType, kind, location);
                if (gpuType is null)
                {
                    return;
                }

                gpuFields.Add(new KeyValuePair<string, GpuTypeShape>(field.Name, gpuType));
                if (CSharpShaderNameMap.IsBoolVector(metadata)
                    || field.ClrType.SpecialType == SpecialType.System_Boolean)
                {
                    AddError(
                        CSharpShaderDiagnosticIds.BoolVectorInLayout,
                        "Boolean fields cannot appear on [GpuLayout] shared structs.",
                        location);
                    return;
                }

                int fieldHostSize;
                int fieldHostAlign;
                if (CSharpShaderNameMap.TryHostScalar(metadata, out GpuScalarKind scalar, out int count))
                {
                    fieldHostSize = GpuLayoutCalculator.HostSequentialSize(scalar, count);
                    fieldHostAlign = GpuLayoutCalculator.HostSequentialAlign(scalar, count);
                }
                else if (field.ClrType.SpecialType is SpecialType.System_Single
                         or SpecialType.System_Int32
                         or SpecialType.System_UInt32)
                {
                    fieldHostSize = 4;
                    fieldHostAlign = 4;
                }
                else
                {
                    fieldHostSize = gpuType.Size;
                    fieldHostAlign = Math.Min(gpuType.Alignment, 4);
                }

                hostOffset = GpuLayoutCalculator.AlignUp(hostOffset, fieldHostAlign);
                if (kind == GpuLayoutKind.ConstantBuffer
                    && gpuType.Alignment >= 16
                    && hostOffset != GpuLayoutCalculator.AlignUp(hostOffset, gpuType.Alignment)
                    && hostOffset % 16 != 0
                    && gpuType.Alignment == 16
                    && fieldHostSize == 12)
                {
                    AddError(
                        CSharpShaderDiagnosticIds.LayoutMismatch,
                        type.Name + "." + field.Name
                        + " host offset " + hostOffset.ToString(CultureInfo.InvariantCulture)
                        + " does not match cbuffer offset "
                        + GpuLayoutCalculator.AlignUp(hostOffset, 16).ToString(CultureInfo.InvariantCulture)
                        + ".",
                        location);
                }

                hostOffset += fieldHostSize;
                if (fieldHostAlign > hostAlign)
                {
                    hostAlign = fieldHostAlign;
                }
            }

            GpuTypeShape gpu = GpuLayoutCalculator.Structure(type.Name, gpuFields, kind);
            int hostSize = GpuLayoutCalculator.AlignUp(hostOffset, hostAlign);
            if (kind == GpuLayoutKind.ConstantBuffer && gpu.Size != hostSize)
            {
                AddError(
                    CSharpShaderDiagnosticIds.LayoutMismatch,
                    type.Name + " host size " + hostSize.ToString(CultureInfo.InvariantCulture)
                    + " does not match cbuffer size "
                    + gpu.Size.ToString(CultureInfo.InvariantCulture) + ".",
                    location);
            }
        }

        private GpuTypeShape? TryGpuShape(ITypeSymbol type, GpuLayoutKind kind, Location location)
        {
            type = Unwrap(type);
            if (type.SpecialType == SpecialType.System_Single)
            {
                return GpuLayoutCalculator.Scalar(GpuScalarKind.Float);
            }

            if (type.SpecialType == SpecialType.System_Int32)
            {
                return GpuLayoutCalculator.Scalar(GpuScalarKind.Int);
            }

            if (type.SpecialType == SpecialType.System_UInt32)
            {
                return GpuLayoutCalculator.Scalar(GpuScalarKind.UInt);
            }

            string metadata = type.ToDisplayString();
            if (CSharpShaderNameMap.TryHostScalar(metadata, out GpuScalarKind scalar, out int count))
            {
                return GpuLayoutCalculator.Vector(scalar, count, kind);
            }

            if (metadata == CSharpShaderNameMap.MathNamespace + ".float4x4")
            {
                return GpuLayoutCalculator.Matrix(GpuScalarKind.Float, 4, 4, kind);
            }

            if (metadata == CSharpShaderNameMap.MathNamespace + ".float3x3")
            {
                return GpuLayoutCalculator.Matrix(GpuScalarKind.Float, 3, 3, kind);
            }

            AddError(
                CSharpShaderDiagnosticIds.UnknownType,
                "Unsupported [GpuLayout] field type '" + metadata + "'.",
                location);
            return null;
        }

        private void CollectCaptures(
            SemanticModel model,
            LocalFunctionStatementSyntax local,
            IMethodSymbol localSymbol,
            List<ISymbol> captures)
        {
            if (local.Body is null)
            {
                return;
            }

            foreach (IdentifierNameSyntax identifier in local.Body.DescendantNodes().OfTypeSafe<IdentifierNameSyntax>())
            {
                ISymbol? symbol = model.GetSymbolInfo(identifier, m_CancellationToken).Symbol;
                if (symbol is null || SymbolEqualityComparer.Default.Equals(symbol.ContainingSymbol, localSymbol))
                {
                    continue;
                }

                if (symbol is ILocalSymbol or IParameterSymbol)
                {
                    if (!ContainsSymbol(captures, symbol))
                    {
                        captures.Add(symbol);
                    }
                }
                else if (symbol is IFieldSymbol field && field.IsStatic)
                {
                    TryAddResource(field);
                }
            }
        }

        private string EmitHlsl(IReadOnlyList<EntryModel> entries)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("#pragma pack_matrix(column_major)");
            builder.AppendLine();

            List<StructModel> structs = new List<StructModel>(m_Structs.Values);
            structs.Sort(static (left, right) => string.CompareOrdinal(left.Name, right.Name));
            for (int index = 0; index < structs.Count; ++index)
            {
                EmitStruct(builder, structs[index]);
            }

            List<ResourceModel> resources = new List<ResourceModel>(m_Resources.Values);
            resources.Sort(static (left, right) =>
            {
                int space = left.Space.CompareTo(right.Space);
                return space != 0
                    ? space
                    : string.CompareOrdinal(left.Name, right.Name);
            });
            for (int index = 0; index < resources.Count; ++index)
            {
                EmitResource(builder, resources[index]);
            }

            if (resources.Count > 0)
            {
                builder.AppendLine();
            }

            List<MethodModel> helpers = new List<MethodModel>();
            foreach (MethodModel method in m_Methods.Values)
            {
                if (!IsEntry(method.Symbol, entries))
                {
                    helpers.Add(method);
                }
            }

            helpers.Sort(static (left, right) => string.CompareOrdinal(left.Symbol.Name, right.Symbol.Name));
            for (int index = 0; index < helpers.Count; ++index)
            {
                EmitMethod(builder, helpers[index], entry: null);
                builder.AppendLine();
            }

            for (int index = 0; index < entries.Count; ++index)
            {
                if (!m_Methods.TryGetValue(entries[index].Method, out MethodModel? method))
                {
                    continue;
                }

                EmitMethod(builder, method, entries[index]);
                builder.AppendLine();
            }

            return builder.ToString();
        }

        private static void EmitStruct(StringBuilder builder, StructModel model)
        {
            builder.Append("struct ");
            builder.Append(model.Name);
            builder.AppendLine();
            builder.AppendLine("{");
            int texcoord = 0;
            for (int index = 0; index < model.Fields.Count; ++index)
            {
                builder.Append("    ");
                builder.Append(model.Fields[index].HlslType);
                builder.Append(' ');
                builder.Append(model.Fields[index].Name);
                if (model.StageInOut)
                {
                    builder.Append(" : TEXCOORD");
                    builder.Append(texcoord.ToString(CultureInfo.InvariantCulture));
                    texcoord++;
                }

                builder.AppendLine(";");
            }

            builder.AppendLine("};");
            builder.AppendLine();
        }

        private static void EmitResource(StringBuilder builder, ResourceModel resource)
        {
            if (resource.GroupShared)
            {
                builder.Append("groupshared ");
                builder.Append(resource.HlslType);
                builder.Append(' ');
                builder.Append(resource.Name);
                builder.AppendLine(";");
                return;
            }

            builder.Append(resource.HlslType);
            builder.Append(' ');
            builder.Append(resource.Name);
            builder.Append(" : register(");
            builder.Append(RegisterLetter(resource.Kind));
            builder.Append(resource.Register.ToString(CultureInfo.InvariantCulture));
            builder.Append(", space");
            builder.Append(resource.Space.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(");");
        }

        private void EmitMethod(StringBuilder builder, MethodModel method, EntryModel? entry)
        {
            if (entry is not null
                && entry.Value.Stage == CSharpShaderStage.Compute
                && entry.Value.ThreadGroup is not null)
            {
                builder.Append("[numthreads(");
                builder.Append(entry.Value.ThreadGroup.X.ToString(CultureInfo.InvariantCulture));
                builder.Append(", ");
                builder.Append(entry.Value.ThreadGroup.Y.ToString(CultureInfo.InvariantCulture));
                builder.Append(", ");
                builder.Append(entry.Value.ThreadGroup.Z.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine(")]");
            }

            builder.Append(MapType(method.Symbol.ReturnType, method.Symbol.Locations.Length > 0 ? method.Symbol.Locations[0] : Location.None));
            builder.Append(' ');
            builder.Append(entry is null ? SanitizedMethodName(method.Symbol) : entry.Value.HlslName);
            builder.Append('(');
            bool first = true;
            foreach (IParameterSymbol parameter in method.Symbol.Parameters)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                AppendParameter(builder, parameter, entry is not null);
            }

            for (int index = 0; index < method.Captures.Count; ++index)
            {
                if (!first)
                {
                    builder.Append(", ");
                }

                first = false;
                ITypeSymbol captureType = method.Captures[index] switch
                {
                    ILocalSymbol local => local.Type,
                    IParameterSymbol parameter => parameter.Type,
                    _ => m_Compilation.GetSpecialType(SpecialType.System_Int32),
                };
                builder.Append(MapType(captureType, Location.None));
                builder.Append(' ');
                builder.Append(method.Captures[index].Name);
            }

            builder.AppendLine(")");
            BlockSyntax? body = method.MethodSyntax?.Body ?? method.LocalSyntax?.Body;
            if (body is null)
            {
                builder.AppendLine("{ }");
                return;
            }

            EmitBlock(builder, method.Model, body, 0, method);
        }

        private void AppendParameter(StringBuilder builder, IParameterSymbol parameter, bool emitSemantic)
        {
            if (parameter.RefKind == RefKind.Out)
            {
                builder.Append("out ");
            }
            else if (parameter.RefKind == RefKind.Ref)
            {
                builder.Append("inout ");
            }

            builder.Append(MapType(parameter.Type, parameter.Locations.Length > 0 ? parameter.Locations[0] : Location.None));
            builder.Append(' ');
            builder.Append(parameter.Name);
            if (!emitSemantic)
            {
                return;
            }

            foreach (AttributeData attribute in parameter.GetAttributes())
            {
                string? name = attribute.AttributeClass?.ToDisplayString();
                if (name is not null && CSharpShaderNameMap.Semantics.TryGetValue(name, out string? semantic))
                {
                    builder.Append(" : ");
                    builder.Append(semantic);
                    return;
                }

                if (name == CSharpShaderNameMap.ShaderLibNamespace + ".SV.TargetAttribute")
                {
                    int index = attribute.ConstructorArguments.Length == 1
                        ? Convert.ToInt32(attribute.ConstructorArguments[0].Value, CultureInfo.InvariantCulture)
                        : 0;
                    builder.Append(" : SV_Target");
                    builder.Append(index.ToString(CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        private void EmitBlock(
            StringBuilder builder,
            SemanticModel model,
            BlockSyntax block,
            int indent,
            MethodModel method)
        {
            AppendIndent(builder, indent);
            builder.AppendLine("{");
            foreach (StatementSyntax statement in block.Statements)
            {
                EmitStatement(builder, model, statement, indent + 1, method);
            }

            AppendIndent(builder, indent);
            builder.AppendLine("}");
        }

        private void EmitStatement(
            StringBuilder builder,
            SemanticModel model,
            StatementSyntax statement,
            int indent,
            MethodModel method)
        {
            m_CancellationToken.ThrowIfCancellationRequested();
            switch (statement)
            {
                case BlockSyntax block:
                    EmitBlock(builder, model, block, indent, method);
                    break;
                case LocalDeclarationStatementSyntax local:
                    EmitLocalDeclaration(builder, model, local, indent, method);
                    break;
                case ExpressionStatementSyntax expression:
                    AppendIndent(builder, indent);
                    builder.Append(LowerExpression(model, expression.Expression, method));
                    builder.AppendLine(";");
                    break;
                case ReturnStatementSyntax ret:
                    AppendIndent(builder, indent);
                    builder.Append("return");
                    if (ret.Expression is not null)
                    {
                        builder.Append(' ');
                        builder.Append(LowerExpression(model, ret.Expression, method));
                    }

                    builder.AppendLine(";");
                    break;
                case IfStatementSyntax ifStatement:
                    AppendIndent(builder, indent);
                    builder.Append("if (");
                    builder.Append(LowerExpression(model, ifStatement.Condition, method));
                    builder.AppendLine(")");
                    EmitEmbedded(builder, model, ifStatement.Statement, indent, method);
                    if (ifStatement.Else is not null)
                    {
                        AppendIndent(builder, indent);
                        builder.AppendLine("else");
                        EmitEmbedded(builder, model, ifStatement.Else.Statement, indent, method);
                    }

                    break;
                case ForStatementSyntax forStatement:
                    AppendIndent(builder, indent);
                    builder.Append("for (");
                    builder.Append(LowerForInitializer(model, forStatement, method));
                    builder.Append("; ");
                    builder.Append(forStatement.Condition is null
                        ? string.Empty
                        : LowerExpression(model, forStatement.Condition, method));
                    builder.Append("; ");
                    builder.Append(LowerForIncrementors(model, forStatement, method));
                    builder.AppendLine(")");
                    EmitEmbedded(builder, model, forStatement.Statement, indent, method);
                    break;
                case WhileStatementSyntax whileStatement:
                    AppendIndent(builder, indent);
                    builder.Append("while (");
                    builder.Append(LowerExpression(model, whileStatement.Condition, method));
                    builder.AppendLine(")");
                    EmitEmbedded(builder, model, whileStatement.Statement, indent, method);
                    break;
                case BreakStatementSyntax:
                    AppendIndent(builder, indent);
                    builder.AppendLine("break;");
                    break;
                case ContinueStatementSyntax:
                    AppendIndent(builder, indent);
                    builder.AppendLine("continue;");
                    break;
                case LocalFunctionStatementSyntax:
                    break;
                default:
                    AddError(
                        CSharpShaderDiagnosticIds.UnsupportedSyntax,
                        "Unsupported statement '" + statement.Kind() + "'.",
                        statement.GetLocation());
                    break;
            }
        }

        private void EmitEmbedded(
            StringBuilder builder,
            SemanticModel model,
            StatementSyntax statement,
            int indent,
            MethodModel method)
        {
            if (statement is BlockSyntax block)
            {
                EmitBlock(builder, model, block, indent, method);
                return;
            }

            EmitStatement(builder, model, statement, indent + 1, method);
        }

        private void EmitLocalDeclaration(
            StringBuilder builder,
            SemanticModel model,
            LocalDeclarationStatementSyntax local,
            int indent,
            MethodModel method)
        {
            foreach (VariableDeclaratorSyntax variable in local.Declaration.Variables)
            {
                ILocalSymbol? symbol = model.GetDeclaredSymbol(variable, m_CancellationToken) as ILocalSymbol;
                string hlslType = symbol is null
                    ? "int"
                    : MapType(symbol.Type, variable.GetLocation());
                if (variable.Initializer?.Value is InvocationExpressionSyntax invocation
                    && model.GetSymbolInfo(invocation, m_CancellationToken).Symbol is IMethodSymbol callee
                    && IntrinsicId(callee) == "RAYDESC_FROM")
                {
                    AppendIndent(builder, indent);
                    builder.Append("RayDesc ");
                    builder.Append(variable.Identifier.ValueText);
                    builder.AppendLine(";");
                    SeparatedSyntaxList<ArgumentSyntax> args = invocation.ArgumentList.Arguments;
                    if (args.Count >= 4)
                    {
                        AppendIndent(builder, indent);
                        builder.Append(variable.Identifier.ValueText);
                        builder.Append(".Origin = ");
                        builder.Append(LowerExpression(model, args[0].Expression, method));
                        builder.AppendLine(";");
                        AppendIndent(builder, indent);
                        builder.Append(variable.Identifier.ValueText);
                        builder.Append(".Direction = ");
                        builder.Append(LowerExpression(model, args[1].Expression, method));
                        builder.AppendLine(";");
                        AppendIndent(builder, indent);
                        builder.Append(variable.Identifier.ValueText);
                        builder.Append(".TMin = ");
                        builder.Append(LowerExpression(model, args[2].Expression, method));
                        builder.AppendLine(";");
                        AppendIndent(builder, indent);
                        builder.Append(variable.Identifier.ValueText);
                        builder.Append(".TMax = ");
                        builder.Append(LowerExpression(model, args[3].Expression, method));
                        builder.AppendLine(";");
                    }

                    m_UsesRayQuery = true;
                    continue;
                }

                AppendIndent(builder, indent);
                if (IsRayQuery(symbol?.Type))
                {
                    builder.Append(MapRayQueryType(model, variable.Initializer?.Value));
                    m_UsesRayQuery = true;
                }
                else
                {
                    builder.Append(hlslType);
                }

                builder.Append(' ');
                builder.Append(variable.Identifier.ValueText);
                if (variable.Initializer is not null
                    && !IsRayQuery(symbol?.Type))
                {
                    builder.Append(" = ");
                    builder.Append(LowerExpression(model, variable.Initializer.Value, method));
                }

                builder.AppendLine(";");
            }
        }

        private string LowerForInitializer(SemanticModel model, ForStatementSyntax forStatement, MethodModel method)
        {
            if (forStatement.Declaration is not null
                && forStatement.Declaration.Variables.Count == 1)
            {
                VariableDeclaratorSyntax variable = forStatement.Declaration.Variables[0];
                ILocalSymbol? symbol = model.GetDeclaredSymbol(variable, m_CancellationToken) as ILocalSymbol;
                string type = symbol is null ? "int" : MapType(symbol.Type, variable.GetLocation());
                string init = variable.Initializer is null
                    ? string.Empty
                    : " = " + LowerExpression(model, variable.Initializer.Value, method);
                return type + " " + variable.Identifier.ValueText + init;
            }

            return LowerExpressionList(model, forStatement.Initializers, method);
        }

        private string LowerForIncrementors(SemanticModel model, ForStatementSyntax forStatement, MethodModel method)
        {
            return LowerExpressionList(model, forStatement.Incrementors, method);
        }

        private string LowerExpressionList(
            SemanticModel model,
            SeparatedSyntaxList<ExpressionSyntax> expressions,
            MethodModel method)
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < expressions.Count; ++index)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(LowerExpression(model, expressions[index], method));
            }

            return builder.ToString();
        }

        private string LowerExpression(SemanticModel model, ExpressionSyntax expression, MethodModel method)
        {
            switch (expression)
            {
                case LiteralExpressionSyntax literal:
                    return LowerLiteral(literal);
                case IdentifierNameSyntax identifier:
                    return MapIdentifier(model, identifier);
                case MemberAccessExpressionSyntax member:
                    return LowerMember(model, member, method);
                case InvocationExpressionSyntax invocation:
                    return LowerInvocation(model, invocation, method);
                case ObjectCreationExpressionSyntax creation:
                    return LowerCreation(model, creation.Type, creation.ArgumentList, method);
                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    return LowerCreation(
                        model,
                        model.GetTypeInfo(implicitCreation, m_CancellationToken).Type,
                        implicitCreation.ArgumentList,
                        method);
                case BinaryExpressionSyntax binary:
                    return "(" + LowerExpression(model, binary.Left, method)
                        + " " + binary.OperatorToken.ValueText + " "
                        + LowerExpression(model, binary.Right, method) + ")";
                case PrefixUnaryExpressionSyntax prefix:
                    return prefix.OperatorToken.ValueText + LowerExpression(model, prefix.Operand, method);
                case PostfixUnaryExpressionSyntax postfix:
                    return LowerExpression(model, postfix.Operand, method) + postfix.OperatorToken.ValueText;
                case ConditionalExpressionSyntax conditional:
                    return "(" + LowerExpression(model, conditional.Condition, method)
                        + " ? " + LowerExpression(model, conditional.WhenTrue, method)
                        + " : " + LowerExpression(model, conditional.WhenFalse, method) + ")";
                case AssignmentExpressionSyntax assignment:
                    return LowerExpression(model, assignment.Left, method)
                        + " " + assignment.OperatorToken.ValueText + " "
                        + LowerExpression(model, assignment.Right, method);
                case ParenthesizedExpressionSyntax parenthesized:
                    return "(" + LowerExpression(model, parenthesized.Expression, method) + ")";
                case CastExpressionSyntax cast:
                    return "((" + MapType(model.GetTypeInfo(cast, m_CancellationToken).Type, cast.GetLocation())
                        + ")" + LowerExpression(model, cast.Expression, method) + ")";
                case ElementAccessExpressionSyntax element:
                    return LowerExpression(model, element.Expression, method)
                        + "[" + LowerArgumentList(model, element.ArgumentList.Arguments, method) + "]";
                case ThisExpressionSyntax:
                    AddError(
                        CSharpShaderDiagnosticIds.UnsupportedSyntax,
                        "'this' is not supported in SharpSL.",
                        expression.GetLocation());
                    return "0";
                default:
                    if (expression is AwaitExpressionSyntax or InterpolatedStringExpressionSyntax)
                    {
                        AddError(
                            CSharpShaderDiagnosticIds.UnsupportedSyntax,
                            "Unsupported expression '" + expression.Kind() + "'.",
                            expression.GetLocation());
                    }

                    return expression.ToString();
            }
        }

        private string LowerMember(SemanticModel model, MemberAccessExpressionSyntax member, MethodModel method)
        {
            ISymbol? symbol = model.GetSymbolInfo(member, m_CancellationToken).Symbol;
            if (symbol is IFieldSymbol field && field.IsStatic && m_Resources.ContainsKey(field))
            {
                return field.Name;
            }

            if (symbol is IPropertySymbol property
                && property.Name == "Value"
                && IsResourceType(property.ContainingType))
            {
                return LowerExpression(model, member.Expression, method);
            }

            if (symbol is IFieldSymbol enumField && enumField.ContainingType?.TypeKind == TypeKind.Enum)
            {
                return MapEnumMember(enumField);
            }

            string left = LowerExpression(model, member.Expression, method);
            string name = member.Name.Identifier.ValueText;
            if (IsSwizzle(name) && IsMathType(model.GetTypeInfo(member.Expression, m_CancellationToken).Type))
            {
                return left + "." + name;
            }

            if (name is "c0" or "c1" or "c2" or "c3")
            {
                int column = name[1] - '0';
                return left + "[" + column.ToString(CultureInfo.InvariantCulture) + "]";
            }

            return left + "." + name;
        }

        private string LowerInvocation(SemanticModel model, InvocationExpressionSyntax invocation, MethodModel method)
        {
            IMethodSymbol? callee = model.GetSymbolInfo(invocation, m_CancellationToken).Symbol as IMethodSymbol;
            if (callee is null)
            {
                AddError(
                    CSharpShaderDiagnosticIds.UnknownIntrinsic,
                    "Unable to bind invocation.",
                    invocation.GetLocation());
                return "0";
            }

            string? intrinsic = IntrinsicId(callee);
            if (intrinsic is not null)
            {
                return LowerIntrinsic(model, invocation, callee, intrinsic, method);
            }

            if (IsMathFunction(callee)
                && CSharpShaderNameMap.MathFunctions.TryGetValue(callee.Name, out string? hlslName))
            {
                return hlslName + "(" + LowerArgumentList(model, invocation.ArgumentList.Arguments, method) + ")";
            }

            if (callee.Name == "lengthsq" && IsMathFunction(callee))
            {
                string argument = LowerArgumentList(model, invocation.ArgumentList.Arguments, method);
                return "dot(" + argument + ", " + argument + ")";
            }

            string name = SanitizedMethodName(callee);
            string args = LowerArgumentList(model, invocation.ArgumentList.Arguments, method);
            if (m_Methods.TryGetValue(callee.OriginalDefinition, out MethodModel? calleeModel)
                && calleeModel.Captures.Count > 0)
            {
                StringBuilder builder = new StringBuilder(args);
                for (int index = 0; index < calleeModel.Captures.Count; ++index)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(", ");
                    }

                    builder.Append(calleeModel.Captures[index].Name);
                }

                args = builder.ToString();
            }

            return name + "(" + args + ")";
        }

        private string LowerIntrinsic(
            SemanticModel model,
            InvocationExpressionSyntax invocation,
            IMethodSymbol callee,
            string intrinsic,
            MethodModel method)
        {
            string args = LowerArgumentList(model, invocation.ArgumentList.Arguments, method);
            string? receiver = null;
            if (invocation.Expression is MemberAccessExpressionSyntax member)
            {
                receiver = LowerExpression(model, member.Expression, method);
            }

            switch (intrinsic)
            {
                case "BUFFER_READ":
                    return receiver + "[" + args + "]";
                case "BUFFER_WRITE":
                    return receiver + "[" + FirstArgument(model, invocation, method) + "] = "
                        + SecondArgument(model, invocation, method);
                case "TEXTURE2D_SAMPLE":
                    return receiver + ".Sample(" + args + ")";
                case "TEXTURE2D_SAMPLE_LEVEL":
                    return receiver + ".SampleLevel(" + args + ")";
                case "TEXTURE2D_LOAD":
                    return receiver + ".Load(" + args + ")";
                case "TEXTURE2D_STORE":
                    return receiver + "[" + FirstArgument(model, invocation, method) + "] = "
                        + SecondArgument(model, invocation, method);
                case "RAY_QUERY_TRACE_RAY_INLINE":
                    m_UsesRayQuery = true;
                    return receiver + ".TraceRayInline("
                        + FirstArgument(model, invocation, method)
                        + ", RAY_FLAG_NONE, "
                        + SecondArgument(model, invocation, method)
                        + ", "
                        + ThirdArgument(model, invocation, method) + ")";
                case "RAY_QUERY_PROCEED":
                    return receiver + ".Proceed()";
                case "RAY_QUERY_TERMINATE":
                    return receiver + ".Abort()";
                case "RAY_QUERY_COMMIT_TRIANGLE":
                    return receiver + ".CommitNonOpaqueTriangleHit()";
                case "RAY_QUERY_COMMITTED_STATUS":
                    return receiver + ".CommittedStatus()";
                case "RAY_QUERY_COMMITTED_TRIANGLE_BARYCENTRICS":
                    return receiver + ".CommittedTriangleBarycentrics()";
                case "RAY_QUERY_COMMITTED_PRIMITIVE_INDEX":
                    return receiver + ".CommittedPrimitiveIndex()";
                case "ALL_MEMORY_BARRIER":
                    return "AllMemoryBarrier()";
                case "GROUP_MEMORY_BARRIER_WITH_GROUP_SYNC":
                    return "GroupMemoryBarrierWithGroupSync()";
                case "INTERLOCKED_ADD":
                    return "InterlockedAdd(" + args + ")";
                case "DDX":
                    return "ddx(" + args + ")";
                case "DDY":
                    return "ddy(" + args + ")";
                case "WAVE_ACTIVE_SUM":
                    m_UsesWaveOperations = true;
                    return "WaveActiveSum(" + args + ")";
                default:
                    AddError(
                        CSharpShaderDiagnosticIds.UnknownIntrinsic,
                        "Unknown intrinsic '" + intrinsic + "'.",
                        invocation.GetLocation());
                    return "0";
            }
        }

        private string LowerCreation(
            SemanticModel model,
            object? typeNode,
            ArgumentListSyntax? arguments,
            MethodModel method)
        {
            ITypeSymbol? type = typeNode as ITypeSymbol;
            if (type is null && typeNode is TypeSyntax typeSyntax)
            {
                type = model.GetTypeInfo(typeSyntax, m_CancellationToken).Type
                    ?? model.GetSymbolInfo(typeSyntax, m_CancellationToken).Symbol as ITypeSymbol;
            }

            string hlslType = MapType(type, arguments?.GetLocation() ?? Location.None);
            if (arguments is null || arguments.Arguments.Count == 0)
            {
                return hlslType + "(0)";
            }

            return hlslType + "(" + LowerArgumentList(model, arguments.Arguments, method) + ")";
        }

        private string LowerArgumentList(
            SemanticModel model,
            SeparatedSyntaxList<ArgumentSyntax> arguments,
            MethodModel method)
        {
            StringBuilder builder = new StringBuilder();
            for (int index = 0; index < arguments.Count; ++index)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }

                builder.Append(LowerExpression(model, arguments[index].Expression, method));
            }

            return builder.ToString();
        }

        private string FirstArgument(SemanticModel model, InvocationExpressionSyntax invocation, MethodModel method)
        {
            return invocation.ArgumentList.Arguments.Count > 0
                ? LowerExpression(model, invocation.ArgumentList.Arguments[0].Expression, method)
                : "0";
        }

        private string SecondArgument(SemanticModel model, InvocationExpressionSyntax invocation, MethodModel method)
        {
            return invocation.ArgumentList.Arguments.Count > 1
                ? LowerExpression(model, invocation.ArgumentList.Arguments[1].Expression, method)
                : "0";
        }

        private string ThirdArgument(SemanticModel model, InvocationExpressionSyntax invocation, MethodModel method)
        {
            return invocation.ArgumentList.Arguments.Count > 2
                ? LowerExpression(model, invocation.ArgumentList.Arguments[2].Expression, method)
                : "0";
        }

        private static string MapIdentifier(SemanticModel model, IdentifierNameSyntax identifier)
        {
            ISymbol? symbol = model.GetSymbolInfo(identifier).Symbol;
            if (symbol is IFieldSymbol field && field.ContainingType?.TypeKind == TypeKind.Enum)
            {
                return MapEnumMember(field);
            }

            return identifier.Identifier.ValueText;
        }

        private static string MapEnumMember(IFieldSymbol field)
        {
            if (field.ContainingType?.ToDisplayString()
                == CSharpShaderNameMap.ShaderLibNamespace + ".HitStatus")
            {
                return field.Name == "HitTriangle"
                    ? "COMMITTED_TRIANGLE_HIT"
                    : "COMMITTED_NOTHING";
            }

            if (field.HasConstantValue)
            {
                return Convert.ToString(field.ConstantValue, CultureInfo.InvariantCulture) ?? "0";
            }

            return field.Name;
        }

        private static string LowerLiteral(LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.DefaultLiteralExpression)
                || literal.IsKind(SyntaxKind.NullLiteralExpression))
            {
                return "0";
            }

            if (literal.IsKind(SyntaxKind.TrueLiteralExpression))
            {
                return "true";
            }

            if (literal.IsKind(SyntaxKind.FalseLiteralExpression))
            {
                return "false";
            }

            if (literal.IsKind(SyntaxKind.NumericLiteralExpression)
                && literal.Token.Value is float or double)
            {
                string text = Convert.ToString(literal.Token.Value, CultureInfo.InvariantCulture) ?? "0";
                if (text.IndexOf('.') < 0 && text.IndexOf('E') < 0 && text.IndexOf('e') < 0)
                {
                    text += ".0";
                }

                return text;
            }

            return literal.Token.ValueText;
        }

        private string MapType(ITypeSymbol? type, Location location)
        {
            if (type is null)
            {
                return "void";
            }

            type = Unwrap(type);
            if (type.SpecialType == SpecialType.System_Void)
            {
                return "void";
            }

            if (TryMapKnownType(type, out string mapped))
            {
                return mapped;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                if (type.ToDisplayString() == CSharpShaderNameMap.ShaderLibNamespace + ".HitStatus")
                {
                    return "uint";
                }

                return "int";
            }

            if (type is INamedTypeSymbol named && m_Structs.ContainsKey(named))
            {
                return named.Name;
            }

            if (IsRayQuery(type))
            {
                return "RayQuery<RAY_FLAG_NONE>";
            }

            if (type.Name == "RayDesc")
            {
                return "RayDesc";
            }

            AddError(
                CSharpShaderDiagnosticIds.UnknownType,
                "Unsupported shader type '" + type.ToDisplayString() + "'.",
                location);
            return "int";
        }

        private static bool TryMapKnownType(ITypeSymbol type, out string hlslType)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Single:
                    hlslType = "float";
                    return true;
                case SpecialType.System_Int32:
                    hlslType = "int";
                    return true;
                case SpecialType.System_UInt32:
                    hlslType = "uint";
                    return true;
                case SpecialType.System_Boolean:
                    hlslType = "bool";
                    return true;
                case SpecialType.System_Double:
                    hlslType = "double";
                    return true;
            }

            return CSharpShaderNameMap.MathTypes.TryGetValue(type.ToDisplayString(), out hlslType!);
        }

        private static bool TryMapResource(
            ITypeSymbol type,
            out CSharpShaderResourceKind kind,
            out string hlslType)
        {
            kind = CSharpShaderResourceKind.StructuredBuffer;
            hlslType = string.Empty;
            if (type is not INamedTypeSymbol named)
            {
                return false;
            }

            string metadata = named.OriginalDefinition.ToDisplayString();
            string element = named.TypeArguments.Length == 1
                ? MapElement(named.TypeArguments[0])
                : "float4";
            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".RWStructuredBuffer<T>")
            {
                kind = CSharpShaderResourceKind.RWStructuredBuffer;
                hlslType = "RWStructuredBuffer<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".StructuredBuffer<T>")
            {
                kind = CSharpShaderResourceKind.StructuredBuffer;
                hlslType = "StructuredBuffer<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".ConstantBuffer<T>")
            {
                kind = CSharpShaderResourceKind.ConstantBuffer;
                hlslType = "ConstantBuffer<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".Texture2D<T>")
            {
                kind = CSharpShaderResourceKind.Texture2D;
                hlslType = "Texture2D<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".RWTexture2D<T>")
            {
                kind = CSharpShaderResourceKind.RWTexture2D;
                hlslType = "RWTexture2D<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".TextureCube<T>")
            {
                kind = CSharpShaderResourceKind.TextureCube;
                hlslType = "TextureCube<" + element + ">";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".ByteAddressBuffer")
            {
                kind = CSharpShaderResourceKind.ByteAddressBuffer;
                hlslType = "ByteAddressBuffer";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".RWByteAddressBuffer")
            {
                kind = CSharpShaderResourceKind.RWByteAddressBuffer;
                hlslType = "RWByteAddressBuffer";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".SamplerState")
            {
                kind = CSharpShaderResourceKind.SamplerState;
                hlslType = "SamplerState";
                return true;
            }

            if (metadata == CSharpShaderNameMap.ShaderLibNamespace + ".RaytracingAccelerationStructure")
            {
                kind = CSharpShaderResourceKind.AccelerationStructure;
                hlslType = "RaytracingAccelerationStructure";
                return true;
            }

            return false;
        }

        private static string MapElement(ITypeSymbol type)
        {
            return TryMapKnownType(Unwrap(type), out string hlsl)
                ? hlsl
                : Unwrap(type).Name;
        }

        private static string RegisterLetter(CSharpShaderResourceKind kind)
        {
            switch (kind)
            {
                case CSharpShaderResourceKind.ConstantBuffer:
                    return "b";
                case CSharpShaderResourceKind.SamplerState:
                    return "s";
                case CSharpShaderResourceKind.RWStructuredBuffer:
                case CSharpShaderResourceKind.RWByteAddressBuffer:
                case CSharpShaderResourceKind.RWTexture2D:
                    return "u";
                default:
                    return "t";
            }
        }

        private static string MapRayQueryType(SemanticModel model, ExpressionSyntax? initializer)
        {
            if (initializer is ObjectCreationExpressionSyntax creation
                && creation.ArgumentList is { Arguments.Count: 1 })
            {
                string text = creation.ArgumentList.Arguments[0].Expression.ToString();
                if (text.IndexOf("AcceptFirst", StringComparison.Ordinal) >= 0)
                {
                    return "RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH>";
                }
            }

            _ = model;
            return "RayQuery<RAY_FLAG_NONE>";
        }

        private static bool IsSwizzle(string name)
        {
            if (name.Length is < 1 or > 4)
            {
                return false;
            }

            for (int index = 0; index < name.Length; ++index)
            {
                char character = name[index];
                if (character is not ('x' or 'y' or 'z' or 'w' or 'r' or 'g' or 'b' or 'a'))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsMathType(ITypeSymbol? type)
        {
            return type is not null && CSharpShaderNameMap.MathTypes.ContainsKey(type.ToDisplayString());
        }

        private static bool IsMathFunction(IMethodSymbol method)
        {
            return method.ContainingType?.ToDisplayString() == CSharpShaderNameMap.MathNamespace + ".math";
        }

        private static bool IsIntrinsic(IMethodSymbol method)
        {
            return IntrinsicId(method) is not null;
        }

        private static string? IntrinsicId(IMethodSymbol method)
        {
            foreach (AttributeData attribute in method.GetAttributes())
            {
                if (attribute.AttributeClass?.ToDisplayString()
                    == CSharpShaderNameMap.ShaderLibNamespace + ".IntrinsicAttribute"
                    && attribute.ConstructorArguments.Length == 1
                    && attribute.ConstructorArguments[0].Value is string id)
                {
                    return id;
                }
            }

            return null;
        }

        private static bool IsResourceType(ITypeSymbol? type)
        {
            return type is not null && TryMapResource(type, out _, out _);
        }

        private static bool IsRayQuery(ITypeSymbol? type)
        {
            return type?.ToDisplayString() == CSharpShaderNameMap.ShaderLibNamespace + ".RayQuery";
        }

        private static bool IsFrameworkType(ITypeSymbol? type)
        {
            if (type is null)
            {
                return true;
            }

            string name = type.ToDisplayString();
            return name.StartsWith("System.", StringComparison.Ordinal)
                || name.StartsWith(CSharpShaderNameMap.MathNamespace, StringComparison.Ordinal)
                || name.StartsWith(CSharpShaderNameMap.ShaderLibNamespace, StringComparison.Ordinal);
        }

        private static ITypeSymbol Unwrap(ITypeSymbol type)
        {
            if (type is INamedTypeSymbol named
                && named.IsGenericType
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1)
            {
                return named.TypeArguments[0];
            }

            return type;
        }

        private static bool IsEntry(IMethodSymbol method, IReadOnlyList<EntryModel> entries)
        {
            for (int index = 0; index < entries.Count; ++index)
            {
                if (SymbolEqualityComparer.Default.Equals(entries[index].Method, method))
                {
                    return true;
                }
            }

            return false;
        }

        private static string SanitizedMethodName(IMethodSymbol method)
        {
            if (method.MethodKind == MethodKind.LocalFunction)
            {
                return method.Name;
            }

            return method.Name;
        }

        private static bool ContainsSymbol(List<ISymbol> symbols, ISymbol symbol)
        {
            for (int index = 0; index < symbols.Count; ++index)
            {
                if (SymbolEqualityComparer.Default.Equals(symbols[index], symbol))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AppendIndent(StringBuilder builder, int indent)
        {
            builder.Append(' ', indent * 4);
        }

        private string BuildDump(IReadOnlyList<EntryModel> entries)
        {
            StringBuilder builder = new StringBuilder();
            builder.AppendLine("entries:");
            for (int index = 0; index < entries.Count; ++index)
            {
                builder.Append("  ");
                builder.Append(entries[index].Stage);
                builder.Append(' ');
                builder.Append(entries[index].HlslName);
                builder.AppendLine();
            }

            builder.AppendLine("resources:");
            List<string> names = new List<string>();
            foreach (ResourceModel resource in m_Resources.Values)
            {
                names.Add(resource.Kind + " " + resource.Name + " b" + resource.Register + " s" + resource.Space);
            }

            names.Sort(StringComparer.Ordinal);
            for (int index = 0; index < names.Count; ++index)
            {
                builder.Append("  ");
                builder.AppendLine(names[index]);
            }

            builder.AppendLine("structs:");
            List<string> structs = new List<string>();
            foreach (StructModel model in m_Structs.Values)
            {
                structs.Add(model.Name);
            }

            structs.Sort(StringComparer.Ordinal);
            for (int index = 0; index < structs.Count; ++index)
            {
                builder.Append("  ");
                builder.AppendLine(structs[index]);
            }

            return builder.ToString();
        }

        private bool HasErrors()
        {
            for (int index = 0; index < m_Diagnostics.Count; ++index)
            {
                if (m_Diagnostics[index].Severity == CSharpShaderDiagnosticSeverity.Error)
                {
                    return true;
                }
            }

            return false;
        }

        private void AddError(string id, string message, Location location)
        {
            m_Diagnostics.Add(CSharpShaderDiagnostic.FromLocation(
                id,
                CSharpShaderDiagnosticSeverity.Error,
                message,
                location));
        }

        private readonly struct EntryModel
        {
            internal EntryModel(
                IMethodSymbol method,
                string hlslName,
                CSharpShaderStage stage,
                CSharpShaderThreadGroup? threadGroup,
                int colorTargetCount)
            {
                Method = method;
                HlslName = hlslName;
                Stage = stage;
                ThreadGroup = threadGroup;
                ColorTargetCount = colorTargetCount;
            }

            internal IMethodSymbol Method { get; }
            internal string HlslName { get; }
            internal CSharpShaderStage Stage { get; }
            internal CSharpShaderThreadGroup? ThreadGroup { get; }
            internal int ColorTargetCount { get; }
        }

        private sealed class MethodModel
        {
            internal MethodModel(
                IMethodSymbol symbol,
                MethodDeclarationSyntax? methodSyntax,
                LocalFunctionStatementSyntax? localSyntax,
                SemanticModel model,
                List<ISymbol> captures)
            {
                Symbol = symbol;
                MethodSyntax = methodSyntax;
                LocalSyntax = localSyntax;
                Model = model;
                Captures = captures;
            }

            internal IMethodSymbol Symbol { get; }
            internal MethodDeclarationSyntax? MethodSyntax { get; }
            internal LocalFunctionStatementSyntax? LocalSyntax { get; }
            internal SemanticModel Model { get; }
            internal List<ISymbol> Captures { get; }
        }

        private sealed class StructModel
        {
            internal StructModel(string name, List<FieldModel> fields, bool stageInOut)
            {
                Name = name;
                Fields = fields;
                StageInOut = stageInOut;
            }

            internal string Name { get; }
            internal List<FieldModel> Fields { get; }
            internal bool StageInOut { get; }
        }

        private readonly struct FieldModel
        {
            internal FieldModel(string name, string hlslType, ITypeSymbol clrType)
            {
                Name = name;
                HlslType = hlslType;
                ClrType = clrType;
            }

            internal string Name { get; }
            internal string HlslType { get; }
            internal ITypeSymbol ClrType { get; }
        }

        private sealed class ResourceModel
        {
            internal ResourceModel(
                string name,
                CSharpShaderResourceKind kind,
                string hlslType,
                int register,
                int space,
                bool pushConstant,
                bool groupShared)
            {
                Name = name;
                Kind = kind;
                HlslType = hlslType;
                Register = register;
                Space = space;
                PushConstant = pushConstant;
                GroupShared = groupShared;
            }

            internal string Name { get; }
            internal CSharpShaderResourceKind Kind { get; }
            internal string HlslType { get; }
            internal int Register { get; }
            internal int Space { get; }
            internal bool PushConstant { get; }
            internal bool GroupShared { get; }
        }
    }

    internal static class SyntaxNodeExtensions
    {
        internal static IEnumerable<T> OfTypeSafe<T>(this IEnumerable<SyntaxNode> nodes)
            where T : SyntaxNode
        {
            foreach (SyntaxNode node in nodes)
            {
                if (node is T typed)
                {
                    yield return typed;
                }
            }
        }

        internal static IEnumerable<T> OfTypeSafe<T>(this IEnumerable<ISymbol> symbols)
            where T : class, ISymbol
        {
            foreach (ISymbol symbol in symbols)
            {
                if (symbol is T typed)
                {
                    yield return typed;
                }
            }
        }
    }
}
