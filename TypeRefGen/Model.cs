namespace TypeRefGen;

internal enum MemberKind {
    Field,
    Property,
    Method,
}

internal sealed class MemberInfoRec {
    public required string Name;
    /// <summary>Field/property type, or method return type.</summary>
    public required CsType Type;
    public bool IsStatic;
    public bool IsPublic;
    public bool CanRead = true;
    public bool CanWrite = true;
    public List<(CsType Type, string Name)> Params = new();
    public MemberKind Kind;
}

internal sealed class TypeRecord {
    public required string Image;
    public required string Namespace;
    public required string Name;
    /// <summary>Namespace.Name; nested types read Outer.Inner.</summary>
    public required string DisplayName;
    public CsType? Base;
    /// <summary>Enum: the underlying integer type.</summary>
    public CsType Underlying = PrimitiveType.Int32;
    public bool IsEnum;
    public bool IsValueType;
    public bool IsInterface;
    public bool IsNested;
    public bool IsPublic;
    public int GenericArity;
    public bool IsCompilerGenerated;
    public List<MemberInfoRec> Fields = new();
    public List<MemberInfoRec> Properties = new();
    public List<MemberInfoRec> Methods = new();
    /// <summary>Public instance constructors; emitted as New(...) helpers.</summary>
    public List<MemberInfoRec> Constructors = new();
    public List<(string Name, string Value)> EnumValues = new();
    public string LookupKey => DisplayName;
}

/// <summary>One loaded dummy assembly and its parsed types.</summary>
internal sealed class AssemblyUnit : IDisposable {
    public required string Image;
    public required string Path;
    public required System.Reflection.PortableExecutable.PEReader Pe;
    public required System.Reflection.Metadata.MetadataReader Reader;
    public List<TypeRecord> Types = new();
    public void Dispose() => Pe.Dispose();
}
