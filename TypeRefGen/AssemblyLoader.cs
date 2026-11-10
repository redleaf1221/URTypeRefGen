using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace TypeRefGen;

/// <summary>
/// Reads dummy assemblies (il2cppdumper DummyDll, Il2CppInspectorRedux output)
/// and turns them into <see cref="TypeRecord"/>s. Only metadata is touched;
/// method bodies do not exist in these files.
/// </summary>
internal static class AssemblyLoader {
    public static AssemblyUnit? Load(string path, Options options, TextWriter log) {
        try {
            var stream = File.OpenRead(path);
            var pe = new PEReader(stream);
            if (!pe.HasMetadata) {
                pe.Dispose();
                stream.Dispose();
                return null;
            }
            var reader = pe.GetMetadataReader();
            var unit = new AssemblyUnit {
                Image = ImageNameOf(reader),
                Path = path,
                Pe = pe,
                Reader = reader,
            };
            ReadTypes(unit, options);
            return unit;
        } catch (Exception ex) {
            log.WriteLine($"  ! skipped {Path.GetFileName(path)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// The IL2CPP image name is the module name without its extension
    /// ("Assembly-CSharp.dll" -> "Assembly-CSharp"); URKit strips .dll too.
    /// </summary>
    private static string ImageNameOf(MetadataReader reader) {
        var module = reader.GetString(reader.GetModuleDefinition().Name);
        return module.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? module[..^4] : module;
    }

    private static void ReadTypes(AssemblyUnit unit, Options options) {
        var reader = unit.Reader;
        CsTypeProvider.CurrentImage = unit.Image;
        foreach (var handle in reader.TypeDefinitions) {
            var definition = reader.GetTypeDefinition(handle);
            var name = reader.GetString(definition.Name);
            if (name == "<Module>")
                continue;

            var visibility = definition.Attributes & TypeAttributes.VisibilityMask;
            var isNested = visibility is TypeAttributes.NestedPublic or TypeAttributes.NestedPrivate
                or TypeAttributes.NestedFamily or TypeAttributes.NestedAssembly
                or TypeAttributes.NestedFamORAssem or TypeAttributes.NestedFamANDAssem;
            var namespc = reader.GetString(definition.Namespace);
            var display = isNested
                ? BuildNestedName(reader, handle)
                : (string.IsNullOrEmpty(namespc) ? name : namespc + "." + name);
            var baseType = CsTypes.FromHandle(reader, definition.BaseType, unit.Image);
            var baseDisplay = (baseType as NamedType)?.Display;

            var record = new TypeRecord {
                Image = unit.Image,
                Namespace = namespc,
                Name = name,
                DisplayName = display,
                Base = baseType,
                IsNested = isNested,
                IsPublic = visibility is TypeAttributes.Public or TypeAttributes.NestedPublic,
                GenericArity = definition.GetGenericParameters().Count,
                IsInterface = (definition.Attributes & TypeAttributes.Interface) != 0,
                IsCompilerGenerated = name.StartsWith('<') || display.Contains("<>"),
                IsEnum = baseDisplay == "System.Enum",
                IsValueType = baseDisplay is "System.ValueType" or "System.Enum",
            };

            if (record.IsEnum)
                ReadEnum(unit, definition, record);
            else if (!record.IsValueType && !record.IsInterface)
                ReadMembers(unit, definition, record, options);

            unit.Types.Add(record);
        }
    }

    private static void ReadMembers(AssemblyUnit unit, TypeDefinition definition, TypeRecord record, Options options) {
        var reader = unit.Reader;

        foreach (var fieldHandle in definition.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            var attributes = field.Attributes;
            var isPublic = (attributes & FieldAttributes.FieldAccessMask) == FieldAttributes.Public;
            if ((attributes & FieldAttributes.Literal) != 0)
                continue; // const: no runtime field storage
            if (!isPublic && !options.IncludeNonPublic)
                continue;
            if (options.MaxMembers > 0 && record.Fields.Count >= options.MaxMembers)
                break;
            var fieldName = reader.GetString(field.Name);
            if (fieldName.EndsWith("k__BackingField", StringComparison.Ordinal))
                continue;
            CsType fieldType;
            try {
                fieldType = field.DecodeSignature(CsTypeProvider.Instance, null);
            } catch {
                continue;
            }
            record.Fields.Add(new MemberInfoRec {
                Name = fieldName,
                Type = fieldType,
                IsStatic = (attributes & FieldAttributes.Static) != 0,
                IsPublic = isPublic,
                Kind = MemberKind.Field,
            });
        }

        foreach (var propertyHandle in definition.GetProperties()) {
            var property = reader.GetPropertyDefinition(propertyHandle);
            var accessors = property.GetAccessors();
            MethodDefinition? getter = accessors.Getter.IsNil ? null : reader.GetMethodDefinition(accessors.Getter);
            MethodDefinition? setter = accessors.Setter.IsNil ? null : reader.GetMethodDefinition(accessors.Setter);
            var access = getter?.Attributes ?? setter?.Attributes ?? 0;
            var isPublic = (access & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            if (!isPublic && !options.IncludeNonPublic)
                continue;
            if (options.MaxMembers > 0 && record.Properties.Count >= options.MaxMembers)
                break;
            var propertyName = reader.GetString(property.Name);
            if (propertyName.StartsWith('<'))
                continue;
            CsType propertyType;
            try {
                propertyType = property.DecodeSignature(CsTypeProvider.Instance, null).ReturnType;
            } catch {
                continue;
            }
            var isStatic = getter is not null
                ? (getter.Value.Attributes & MethodAttributes.Static) != 0
                : setter is not null && (setter.Value.Attributes & MethodAttributes.Static) != 0;
            var canWrite = setter is not null
                && (setter.Value.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            record.Properties.Add(new MemberInfoRec {
                Name = propertyName,
                Type = propertyType,
                IsStatic = isStatic,
                IsPublic = isPublic,
                CanRead = getter is not null,
                CanWrite = canWrite,
                Kind = MemberKind.Property,
            });
        }

        if (!options.Methods)
            return;

        foreach (var methodHandle in definition.GetMethods()) {
            var method = reader.GetMethodDefinition(methodHandle);
            var attributes = method.Attributes;
            var methodName = reader.GetString(method.Name);
            if (methodName == ".ctor") {
                ReadConstructor(reader, methodHandle, record);
                continue;
            }
            if ((attributes & MethodAttributes.SpecialName) != 0)
                continue; // accessors, operators and the static constructor
            if ((attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
                continue;
            if ((attributes & MethodAttributes.Abstract) != 0)
                continue;
            if (method.GetGenericParameters().Count > 0)
                continue; // URKit's Call cannot supply generic arguments
            if (options.MaxMembers > 0 && record.Methods.Count >= options.MaxMembers)
                break;
            MethodSignature<CsType> signature;
            try {
                signature = method.DecodeSignature(CsTypeProvider.Instance, null);
            } catch {
                continue;
            }
            if (signature.GenericParameterCount > 0)
                continue;
            var member = new MemberInfoRec {
                Name = methodName,
                Type = signature.ReturnType,
                IsStatic = (attributes & MethodAttributes.Static) != 0,
                IsPublic = true,
                Kind = MemberKind.Method,
            };
            for (var i = 0; i < signature.ParameterTypes.Length; i++)
                member.Params.Add((signature.ParameterTypes[i], "arg" + i));
            record.Methods.Add(member);
        }
    }

    private static void ReadConstructor(MetadataReader reader, MethodDefinitionHandle handle, TypeRecord record) {
        var method = reader.GetMethodDefinition(handle);
        var attributes = method.Attributes;
        if ((attributes & MethodAttributes.MemberAccessMask) != MethodAttributes.Public)
            return;
        if ((attributes & MethodAttributes.Static) != 0)
            return; // .cctor
        if ((attributes & MethodAttributes.Abstract) != 0)
            return;
        if (method.GetGenericParameters().Count > 0)
            return;
        MethodSignature<CsType> signature;
        try {
            signature = method.DecodeSignature(CsTypeProvider.Instance, null);
        } catch {
            return;
        }
        if (signature.GenericParameterCount > 0)
            return;
        var member = new MemberInfoRec {
            Name = ".ctor",
            Type = signature.ReturnType,
            IsPublic = true,
            Kind = MemberKind.Method,
        };
        for (var i = 0; i < signature.ParameterTypes.Length; i++)
            member.Params.Add((signature.ParameterTypes[i], "arg" + i));
        record.Constructors.Add(member);
    }

    private static void ReadEnum(AssemblyUnit unit, TypeDefinition definition, TypeRecord record) {
        var reader = unit.Reader;
        foreach (var fieldHandle in definition.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            var name = reader.GetString(field.Name);
            if (name == "value__") {
                record.Underlying = field.DecodeSignature(CsTypeProvider.Instance, null);
                continue;
            }
            if ((field.Attributes & FieldAttributes.Literal) == 0)
                continue;
            var value = ConstantValue(reader, field);
            record.EnumValues.Add((name, value?.ToString() ?? "0"));
        }
    }

    private static object? ConstantValue(MetadataReader reader, FieldDefinition field) {
        var handle = field.GetDefaultValue();
        if (handle.IsNil)
            return null;
        var constant = reader.GetConstant(handle);
        var bytes = reader.GetBlobBytes(constant.Value);
        try {
            return constant.TypeCode switch {
                ConstantTypeCode.Boolean => bytes.Length > 0 && bytes[0] != 0,
                ConstantTypeCode.Char => BitConverter.ToUInt16(bytes, 0),
                ConstantTypeCode.SByte => (sbyte)bytes[0],
                ConstantTypeCode.Byte => bytes[0],
                ConstantTypeCode.Int16 => BitConverter.ToInt16(bytes, 0),
                ConstantTypeCode.UInt16 => BitConverter.ToUInt16(bytes, 0),
                ConstantTypeCode.Int32 => BitConverter.ToInt32(bytes, 0),
                ConstantTypeCode.UInt32 => BitConverter.ToUInt32(bytes, 0),
                ConstantTypeCode.Int64 => BitConverter.ToInt64(bytes, 0),
                ConstantTypeCode.UInt64 => BitConverter.ToUInt64(bytes, 0),
                _ => null,
            };
        } catch {
            return null;
        }
    }

    private static string BuildNestedName(MetadataReader reader, TypeDefinitionHandle handle) {
        var names = new List<string>();
        var current = handle;
        while (true) {
            var definition = reader.GetTypeDefinition(current);
            names.Insert(0, reader.GetString(definition.Name));
            var declaring = definition.GetDeclaringType();
            if (declaring.IsNil)
                break;
            current = declaring;
        }
        return string.Join(".", names);
    }
}
