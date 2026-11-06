namespace TypeRefGen;

/// <summary>Command line parsing. Keep the surface flat and documented in PrintUsage.</summary>
internal static class Cli {
    public static void PrintUsage(TextWriter w) {
        w.WriteLine("""
URTypeRefGen - URKit TypeRef / typed-accessor generator for il2cpp dummy assemblies

Usage:
  TypeRefGen --dll-dir <dir> [options]

Input:
  --dll-dir <dir>         Directory with dummy .dll files (required)
  --assembly <name>       Restrict to this image (repeatable; default Assembly-CSharp)
  --all-assemblies        Ignore the default Assembly-CSharp filter

Selection:
  --type <Ns.Class>       Exact type to emit (repeatable)
  --match <glob>          Type glob, e.g. "*Alien*" or "Shapes2D.*" (repeatable)
  --exclude <glob>        Exclude glob (repeatable)
  --max <n>               Cap the selected type count
  --include-nested        Allow nested types (URKit cannot resolve them today)
  --include-generics      Allow open generic types
  --include-non-public    Allow internal/private types and members
  --include-value-types   Allow plain structs (TypeRef only, no wrapper)
  --list                  List matching types and exit
  --list-members          With --list, print member signatures too

Code generation:
  --out <file>            Header to write (default: stdout)
  --namespace <name>      C++ namespace for generated symbols (default GameTypes)
  --methods               Emit method wrappers (default: fields and properties only)
  --exact-methods         Emit CallExact for same-name same-arity overloads (instance only)
  --no-fields             Skip field accessors
  --no-properties         Skip property accessors
  --no-find-helpers       Skip Find()/FindAll() on UnityEngine.Object types
  --no-self-check         Skip the -DURTYPEGEN_SELF_CHECK block
  --max-members <n>       Cap members per type
  --report <file>         Per-type report of emitted and skipped members
  --verbose               Print per-assembly load details

Examples:
  TypeRefGen --dll-dir .\DummyDllTest --list
  TypeRefGen --dll-dir .\DummyDllTest --match "*Alien*" --out ..\mod\game\game_types.h
  TypeRefGen --dll-dir .\DummyDllTest --type Global --type Global.SaveData --methods --out out.h
""");
    }

    public static Options? Parse(string[] argv, TextWriter error) {
        var o = new Options();
        for (var i = 0; i < argv.Length; i++) {
            var arg = argv[i];
            string Next() {
                if (i + 1 >= argv.Length)
                    throw new ArgumentException($"missing value for {arg}");
                return argv[++i];
            }
            try {
                switch (arg) {
                    case "--dll-dir": o.DllDir = Next(); break;
                    case "--out": o.OutFile = Next(); break;
                    case "--namespace": o.Namespace = Next(); break;
                    case "--assembly": o.Assemblies.Add(Next()); break;
                    case "--all-assemblies": o.AllAssemblies = true; break;
                    case "--type": o.Types.Add(Next()); break;
                    case "--match": o.Matches.Add(Next()); break;
                    case "--exclude": o.Excludes.Add(Next()); break;
                    case "--max": o.Max = int.Parse(Next()); break;
                    case "--max-members": o.MaxMembers = int.Parse(Next()); break;
                    case "--list": o.List = true; break;
                    case "--list-members": o.ListMembers = true; o.List = true; break;
                    case "--methods": o.Methods = true; break;
                    case "--exact-methods": o.Methods = true; o.ExactMethods = true; break;
                    case "--no-fields": o.Fields = false; break;
                    case "--no-properties": o.Properties = false; break;
                    case "--no-find-helpers": o.FindHelpers = false; break;
                    case "--no-self-check": o.SelfCheck = false; break;
                    case "--include-nested": o.IncludeNested = true; break;
                    case "--include-generics": o.IncludeGenerics = true; break;
                    case "--include-value-types": o.IncludeValueTypes = true; break;
                    case "--include-non-public": o.IncludeNonPublic = true; break;
                    case "--report": o.ReportFile = Next(); break;
                    case "--verbose": o.Verbose = true; break;
                    case "-h" or "--help" or "/?":
                        return null;
                    default:
                        error.WriteLine($"error: unknown option {arg}");
                        return null;
                }
            } catch (Exception ex) {
                error.WriteLine($"error: {ex.Message}");
                return null;
            }
        }
        o.ApplyDefaults();
        return o;
    }
}
