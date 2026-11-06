using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace TypeRefGen;

/// <summary>
/// A decoded metadata type signature. Everything downstream (mapping, overload
/// resolution, comments, emission) pattern-matches on this tree instead of
/// parsing type names as text. Strings appear only where a literal is required
/// (the emitted TypeRef fields and the runtime parameter type names).
/// </summary>
internal abstract class CsType {
    /// <summary>
    /// The name the il2cpp runtime reports for this type, i.e. what
    /// il2cpp_type_get_name returns: System.Int32, Namespace.Class, T[],
    /// System.Collections.Generic.List`1&lt;T&gt;.
    /// </summary>
    public abstract string RuntimeName { get; }
}

internal sealed class PrimitiveType : CsType {
    private static readonly Dictionary<PrimitiveTypeCode, (string Runtime, string Alias)> Names = new() {
        [PrimitiveTypeCode.Void] = ("System.Void", "void"),
        [PrimitiveTypeCode.Boolean] = ("System.Boolean", "bool"),
        [PrimitiveTypeCode.Char] = ("System.Char", "char"),
        [PrimitiveTypeCode.SByte] = ("System.SByte", "sbyte"),
        [PrimitiveTypeCode.Byte] = ("System.Byte", "byte"),
        [PrimitiveTypeCode.Int16] = ("System.Int16", "short"),
        [PrimitiveTypeCode.UInt16] = ("System.UInt16", "ushort"),
        [PrimitiveTypeCode.Int32] = ("System.Int32", "int"),
        [PrimitiveTypeCode.UInt32] = ("System.UInt32", "uint"),
        [PrimitiveTypeCode.Int64] = ("System.Int64", "long"),
        [PrimitiveTypeCode.UInt64] = ("System.UInt64", "ulong"),
        [PrimitiveTypeCode.Single] = ("System.Single", "float"),
        [PrimitiveTypeCode.Double] = ("System.Double", "double"),
        [PrimitiveTypeCode.String] = ("System.String", "string"),
        [PrimitiveTypeCode.Object] = ("System.Object", "object"),
        [PrimitiveTypeCode.IntPtr] = ("System.IntPtr", "nint"),
        [PrimitiveTypeCode.UIntPtr] = ("System.UIntPtr", "nuint"),
        [PrimitiveTypeCode.TypedReference] = ("System.TypedReference", "typedref"),
    };

    public PrimitiveType(PrimitiveTypeCode code) => Code = code;

    public PrimitiveTypeCode Code { get; }
    public override string RuntimeName => Names.TryGetValue(Code, out var name) ? name.Runtime : Code.ToString();
    public string Alias => Names.TryGetValue(Code, out var name) ? name.Alias : Code.ToString();

    public static readonly PrimitiveType Int32 = new(PrimitiveTypeCode.Int32);
    public static readonly PrimitiveType Boolean = new(PrimitiveTypeCode.Boolean);
    public static readonly PrimitiveType Void = new(PrimitiveTypeCode.Void);
}

/// <summary>A named class, struct, interface or enum, optionally resolved to a loaded record.</summary>
internal sealed class NamedType : CsType {
    public NamedType(string namespc, string name, string sourceImage) {
        Namespace = namespc;
        Name = name;
        SourceImage = sourceImage;
    }

    public string Namespace { get; }
    public string Name { get; }
    /// <summary>Image the reference was decoded from; used to prefer the same assembly when binding.</summary>
    public string SourceImage { get; }
    public string Display => string.IsNullOrEmpty(Namespace) ? Name : Namespace + "." + Name;
    public override string RuntimeName => Display;

    /// <summary>Filled once by <see cref="TypeIndex"/> after every assembly is loaded.</summary>
    public TypeRecord? Resolved { get; set; }
}

internal sealed class GenericInstanceType : CsType {
    public GenericInstanceType(NamedType definition, IReadOnlyList<CsType> arguments) {
        Definition = definition;
        Arguments = arguments;
    }

    public NamedType Definition { get; }
    public IReadOnlyList<CsType> Arguments { get; }
    public override string RuntimeName =>
        Definition.RuntimeName + "<" + string.Join(", ", Arguments.Select(a => a.RuntimeName)) + ">";
}

internal sealed class ArrayType : CsType {
    public ArrayType(CsType element, int rank) {
        Element = element;
        Rank = rank;
    }

    public CsType Element { get; }
    public int Rank { get; }
    public override string RuntimeName => Element.RuntimeName + "[" + new string(',', Math.Max(0, Rank - 1)) + "]";
}

internal sealed class ByRefType : CsType {
    public ByRefType(CsType element) => Element = element;
    public CsType Element { get; }
    public override string RuntimeName => Element.RuntimeName + "&";
}

internal sealed class PointerType : CsType {
    public PointerType(CsType element) => Element = element;
    public CsType Element { get; }
    public override string RuntimeName => Element.RuntimeName + "*";
}

internal sealed class GenericParameterType : CsType {
    public GenericParameterType(bool isMethodParameter, int index) {
        IsMethodParameter = isMethodParameter;
        Index = index;
    }

    public bool IsMethodParameter { get; }
    public int Index { get; }
    public override string RuntimeName => (IsMethodParameter ? "!!" : "!") + Index;
}

internal sealed class FunctionPointerType : CsType {
    public override string RuntimeName => "System.IntPtr";
}

/// <summary>Builds <see cref="CsType"/> trees from metadata signature blobs.</summary>
internal sealed class CsTypeProvider : ISignatureTypeProvider<CsType, object?> {
    public static readonly CsTypeProvider Instance = new();

    public CsType GetPrimitiveType(PrimitiveTypeCode code) => new PrimitiveType(code);

    public CsType GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) {
        var definition = reader.GetTypeDefinition(handle);
        return new NamedType(reader.GetString(definition.Namespace), reader.GetString(definition.Name),
            CurrentImage);
    }

    public CsType GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        var namespc = reader.GetString(reference.Namespace);
        // A nested type reference points at its enclosing type; keep the C#
        // nesting spelling so the record lookup can find it.
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference) {
            var outer = GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind);
            if (outer is NamedType outerNamed)
                return new NamedType(outerNamed.Namespace, outerNamed.Name + "." + name, CurrentImage);
        }
        return new NamedType(namespc, name, CurrentImage);
    }

    public CsType GetTypeFromSpecification(MetadataReader reader, object? genericContext,
                                           TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public CsType GetSZArrayType(CsType elementType) => new ArrayType(elementType, 1);
    public CsType GetArrayType(CsType elementType, ArrayShape shape) => new ArrayType(elementType, shape.Rank);
    public CsType GetByReferenceType(CsType elementType) => new ByRefType(elementType);
    public CsType GetPointerType(CsType elementType) => new PointerType(elementType);
    public CsType GetGenericInstantiation(CsType genericType, ImmutableArray<CsType> typeArguments) =>
        new GenericInstanceType(genericType as NamedType
                                ?? new NamedType("", genericType.RuntimeName, CurrentImage),
            typeArguments.ToArray());
    public CsType GetGenericTypeParameter(object? genericContext, int index) => new GenericParameterType(false, index);
    public CsType GetGenericMethodParameter(object? genericContext, int index) => new GenericParameterType(true, index);
    public CsType GetFunctionPointerType(MethodSignature<CsType> signature) => new FunctionPointerType();
    public CsType GetModifiedType(CsType modifier, CsType unmodifiedType, bool isRequired) => unmodifiedType;
    public CsType GetPinnedType(CsType elementType) => elementType;

    /// <summary>Set while a unit is being read so nested references know their image.</summary>
    internal static string CurrentImage = "";
}

internal static class CsTypes {
    /// <summary>Decodes a bare EntityHandle (base type, element type) into a CsType.</summary>
    public static CsType? FromHandle(MetadataReader reader, EntityHandle handle, string image) {
        if (handle.IsNil)
            return null;
        switch (handle.Kind) {
            case HandleKind.TypeDefinition:
                return CsTypeProvider.Instance.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0);
            case HandleKind.TypeReference:
                return CsTypeProvider.Instance.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);
            case HandleKind.TypeSpecification:
                return reader.GetTypeSpecification((TypeSpecificationHandle)handle)
                    .DecodeSignature(CsTypeProvider.Instance, null);
        }
        return null;
    }

    /// <summary>Depth-first visit of every child type in the tree.</summary>
    public static IEnumerable<CsType> Descendants(this CsType type) {
        yield return type;
        switch (type) {
            case GenericInstanceType generic:
                yield return generic.Definition;
                foreach (var argument in generic.Arguments)
                foreach (var child in argument.Descendants())
                    yield return child;
                break;
            case ArrayType array:
                foreach (var child in array.Element.Descendants())
                    yield return child;
                break;
            case ByRefType byRef:
                foreach (var child in byRef.Element.Descendants())
                    yield return child;
                break;
            case PointerType pointer:
                foreach (var child in pointer.Element.Descendants())
                    yield return child;
                break;
        }
    }
}
