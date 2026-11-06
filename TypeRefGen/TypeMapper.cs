using System.Reflection.Metadata;
using System.Text;

namespace TypeRefGen;

/// <summary>
/// Maps decoded <see cref="CsType"/> trees onto C++ types that URKit's
/// GetField / SetField / Call templates marshal safely.
///
/// Decisions are structural (AST node kind, resolved record identity), never
/// parsed out of a type name. Anything unsupported returns null and is
/// reported instead of guessed.
/// </summary>
internal sealed class TypeMapper {
    private static readonly Dictionary<PrimitiveTypeCode, string> PrimitiveMap = new() {
        [PrimitiveTypeCode.Void] = "void",
        [PrimitiveTypeCode.Boolean] = "bool",
        [PrimitiveTypeCode.Byte] = "std::uint8_t",
        [PrimitiveTypeCode.SByte] = "std::int8_t",
        [PrimitiveTypeCode.Char] = "std::uint16_t",
        [PrimitiveTypeCode.Int16] = "std::int16_t",
        [PrimitiveTypeCode.UInt16] = "std::uint16_t",
        [PrimitiveTypeCode.Int32] = "std::int32_t",
        [PrimitiveTypeCode.UInt32] = "std::uint32_t",
        [PrimitiveTypeCode.Int64] = "std::int64_t",
        [PrimitiveTypeCode.UInt64] = "std::uint64_t",
        [PrimitiveTypeCode.Single] = "float",
        [PrimitiveTypeCode.Double] = "double",
        [PrimitiveTypeCode.IntPtr] = "void *",
        [PrimitiveTypeCode.UIntPtr] = "void *",
        [PrimitiveTypeCode.String] = "std::string",
        [PrimitiveTypeCode.Object] = "Object",
    };

    /// <summary>UnityEngine value types with a hand-written POD mirror in the URKit SDK.</summary>
    private static readonly Dictionary<string, string> UnityPodMap = new(StringComparer.Ordinal) {
        ["UnityEngine.Vector2"] = "::URK::Unity::Vector2",
        ["UnityEngine.Vector3"] = "::URK::Unity::Vector3",
        ["UnityEngine.Vector4"] = "::URK::Unity::Vector4",
        ["UnityEngine.Quaternion"] = "::URK::Unity::Quaternion",
        ["UnityEngine.Color"] = "::URK::Unity::Color",
        ["UnityEngine.Rect"] = "::URK::Unity::Rect",
        ["UnityEngine.Bounds"] = "::URK::Unity::Bounds",
    };

    /// <summary>Readable spellings for comments only; identity lookups on whole names.</summary>
    private static readonly Dictionary<string, string> ShortAliasMap = new(StringComparer.Ordinal) {
        ["System.Void"] = "void", ["System.Boolean"] = "bool", ["System.Byte"] = "byte",
        ["System.SByte"] = "sbyte", ["System.Char"] = "char", ["System.Int16"] = "short",
        ["System.UInt16"] = "ushort", ["System.Int32"] = "int", ["System.UInt32"] = "uint",
        ["System.Int64"] = "long", ["System.UInt64"] = "ulong", ["System.Single"] = "float",
        ["System.Double"] = "double", ["System.String"] = "string", ["System.Object"] = "object",
        ["System.IntPtr"] = "nint", ["System.UIntPtr"] = "nuint",
    };

    private static readonly HashSet<string> UnityObjectBases = new(StringComparer.Ordinal) {
        "UnityEngine.Object", "UnityEngine.Component", "UnityEngine.Behaviour",
        "UnityEngine.MonoBehaviour", "UnityEngine.ScriptableObject",
    };

    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) {
        // C++ keywords plus the generated wrapper's own member names.
        "alignas","alignof","and","and_eq","asm","auto","bitand","bitor","bool","break","case","catch","char",
        "char8_t","char16_t","char32_t","class","compl","concept","const","consteval","constexpr","constinit",
        "const_cast","continue","co_await","co_return","co_yield","decltype","default","delete","do","double",
        "dynamic_cast","else","enum","explicit","export","extern","false","float","for","friend","goto","if",
        "inline","int","long","mutable","namespace","new","noexcept","not","not_eq","nullptr","operator","or",
        "or_eq","private","protected","public","register","reinterpret_cast","requires","return","short","signed",
        "sizeof","static","static_assert","static_cast","struct","switch","template","this","thread_local","throw",
        "true","try","typedef","typeid","typename","union","unsigned","using","virtual","void","volatile","wchar_t",
        "while","xor","xor_eq",
        "o","type","handle","value","Object","TypeRef",
    };

    private readonly HashSet<TypeRecord> _emittedEnums = new(ReferenceEqualityComparer.Instance);

    public TypeMapper(IEnumerable<TypeRecord> selected) {
        foreach (var record in selected.Where(r => r.IsEnum))
            _emittedEnums.Add(record);
    }

    /// <summary>Returns a marshalable C++ type name, or null when unsupported.</summary>
    public string? Map(CsType type) => type switch {
        PrimitiveType primitive => PrimitiveMap.TryGetValue(primitive.Code, out var cpp) ? cpp : null,
        FunctionPointerType => "void *",
        NamedType named => MapNamed(named),
        // Generics, arrays, by-refs, pointers and open generic parameters have no
        // safe field/call marshalling in the runtime API.
        _ => null,
    };

    private string? MapNamed(NamedType named) {
        var record = named.Resolved;
        if (record is null)
            return null;
        if (record.IsEnum)
            return _emittedEnums.Contains(record) ? EnumCppName(record) : UnderlyingCpp(record);
        if (record.IsValueType)
            return UnityPodMap.TryGetValue(record.DisplayName, out var pod) ? pod : null;
        return "Object"; // class or interface reference
    }

    public string UnderlyingCpp(TypeRecord record) => Map(record.Underlying) ?? "std::int32_t";

    /// <summary>Enums are flattened into the generated namespace: Ns_Type.</summary>
    public static string EnumCppName(TypeRecord record) =>
        string.IsNullOrEmpty(record.Namespace)
            ? SafeIdentifier(record.Name)
            : SafeIdentifier(record.Namespace) + "_" + SafeIdentifier(record.Name);

    public bool DerivesFromUnityObject(TypeRecord record) {
        var current = record.Base as NamedType;
        for (var guard = 0; current is not null && guard < 64; guard++) {
            if (UnityObjectBases.Contains(current.Display))
                return true;
            current = current.Resolved?.Base as NamedType;
        }
        return false;
    }

    public static string SafeIdentifier(string name) {
        var sb = new StringBuilder(name.Length + 2);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        if (sb.Length == 0)
            sb.Append('_');
        if (char.IsDigit(sb[0]))
            sb.Insert(0, '_');
        var result = sb.ToString();
        return Reserved.Contains(result) ? result + "_" : result;
    }

    /// <summary>Readable C# spelling for comments; never re-parses the name.</summary>
    public static string ShortType(CsType type) => type switch {
        PrimitiveType primitive => ShortAliasMap.TryGetValue(primitive.RuntimeName, out var alias)
            ? alias
            : primitive.RuntimeName,
        NamedType named => ShortAliasMap.TryGetValue(named.Display, out var alias) ? alias : named.Display,
        GenericInstanceType generic => ShortType(generic.Definition) + "<" +
                                       string.Join(", ", generic.Arguments.Select(ShortType)) + ">",
        ArrayType array => ShortType(array.Element) + "[" + new string(',', Math.Max(0, array.Rank - 1)) + "]",
        ByRefType byRef => ShortType(byRef.Element) + "&",
        PointerType pointer => ShortType(pointer.Element) + "*",
        GenericParameterType parameter => (parameter.IsMethodParameter ? "TMethod" : "T") + parameter.Index,
        _ => "fnptr",
    };

    /// <summary>Readable method signature for comments: void Apply(int, string).</summary>
    public static string ShortMethod(MemberInfoRec member) =>
        $"{ShortType(member.Type)} {member.Name}({string.Join(", ", member.Params.Select(p => ShortType(p.Type)))})";
}
