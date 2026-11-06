using System.Text;
using System.Text.RegularExpressions;

namespace TypeRefGen;

/// <summary>Picks which types end up in the header.</summary>
internal static class Selector {
    public static bool MatchesGlob(string text, string pattern) {
        if (pattern.Length == 0)
            return false;
        var sb = new StringBuilder("^");
        foreach (var ch in pattern) {
            sb.Append(ch switch {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(ch.ToString()),
            });
        }
        sb.Append('$');
        return Regex.IsMatch(text, sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

    public static List<TypeRecord> Select(List<TypeRecord> all, Options o, TextWriter log) {
        var selected = new List<TypeRecord>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var explicitRequest = o.Types.Count > 0 || o.Matches.Count > 0;

        foreach (var t in all) {
            if (!o.AllAssemblies
                && o.Assemblies.Count > 0
                && !o.Assemblies.Any(a => t.Image.Equals(a, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (t.IsCompilerGenerated)
                continue;
            if (t.IsNested && !o.IncludeNested)
                continue;
            if (t.GenericArity > 0 && !o.IncludeGenerics)
                continue;
            if (!t.IsPublic && !o.IncludeNonPublic)
                continue;
            if (!explicitRequest && t.IsInterface)
                continue;
            if (!explicitRequest && t.IsValueType && !t.IsEnum && !o.IncludeValueTypes)
                continue;

            var take = !explicitRequest
                       || o.Types.Any(x => SameType(t, x))
                       || o.Matches.Any(p => MatchesGlob(t.DisplayName, p) || MatchesGlob(t.Name, p));
            if (!take)
                continue;
            if (o.Excludes.Any(p => MatchesGlob(t.DisplayName, p)))
                continue;
            if (seen.Add(t.Image + "|" + t.LookupKey))
                selected.Add(t);
        }

        if (explicitRequest) {
            foreach (var wanted in o.Types.Where(w => !selected.Any(t => SameType(t, w))))
                log.WriteLine($"  ! requested type not found: {wanted}");
        }

        if (o.Max > 0 && selected.Count > o.Max)
            selected = selected.Take(o.Max).ToList();

        selected.Sort((a, b) => string.CompareOrdinal(a.DisplayName, b.DisplayName));
        return selected;
    }

    private static bool SameType(TypeRecord t, string wanted) {
        var w = wanted.Trim();
        return t.DisplayName.Equals(w, StringComparison.OrdinalIgnoreCase)
               || (t.Namespace + "." + t.Name).Equals(w, StringComparison.OrdinalIgnoreCase);
    }
}
