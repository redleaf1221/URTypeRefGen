namespace TypeRefGen;

/// <summary>
/// Resolves <see cref="NamedType"/> nodes to loaded <see cref="TypeRecord"/>s.
///
/// Cross-assembly references in metadata are name-based, so one name lookup per
/// distinct reference is unavoidable; it happens exactly once here and the
/// result is cached on the node. Nothing downstream re-parses or re-compares
/// type names.
/// </summary>
internal sealed class TypeIndex {
    private readonly Dictionary<string, TypeRecord> _byImageAndDisplay = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TypeRecord> _byDisplay = new(StringComparer.Ordinal);

    public TypeIndex(IEnumerable<TypeRecord> all) {
        foreach (var record in all) {
            _byImageAndDisplay.TryAdd(Key(record.Image, record.DisplayName), record);
            _byDisplay.TryAdd(record.DisplayName, record);
        }
    }

    private static string Key(string image, string display) => image + "|" + display;

    public TypeRecord? Find(string display) =>
        _byDisplay.TryGetValue(display, out var record) ? record : null;

    /// <summary>Prefers a record from the image the reference came from.</summary>
    public TypeRecord? Find(NamedType named) =>
        _byImageAndDisplay.TryGetValue(Key(named.SourceImage, named.Display), out var local)
            ? local
            : Find(named.Display);

    /// <summary>Binds every named type reachable from the given records.</summary>
    public void BindAll(IEnumerable<TypeRecord> records) {
        foreach (var record in records) {
            Bind(record.Base);
            Bind(record.Underlying);
            foreach (var field in record.Fields)
                Bind(field.Type);
            foreach (var property in record.Properties)
                Bind(property.Type);
            foreach (var method in record.Methods) {
                Bind(method.Type);
                foreach (var (parameterType, _) in method.Params)
                    Bind(parameterType);
            }
        }
    }

    public void Bind(CsType? type) {
        if (type is null)
            return;
        foreach (var node in type.Descendants())
            if (node is NamedType named && named.Resolved is null)
                named.Resolved = Find(named);
    }
}
