using System.Text;

namespace TypeRefGen;

internal static class Program {
    public static int Main(string[] argv) {
        var log = Console.Error;
        var options = Cli.Parse(argv, log);
        if (options is null) {
            Cli.PrintUsage(log);
            return 2;
        }

        if (options.DllDir.Length == 0) {
            log.WriteLine("error: --dll-dir is required");
            return 2;
        }
        if (!Directory.Exists(options.DllDir)) {
            log.WriteLine($"error: --dll-dir not found: {options.DllDir}");
            return 2;
        }

        var units = new List<AssemblyUnit>();
        foreach (var file in Directory.GetFiles(options.DllDir, "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
            var unit = AssemblyLoader.Load(file, options, log);
            if (unit is null)
                continue;
            if (options.Verbose || options.List)
                log.WriteLine($"  + {unit.Image,-40} {unit.Types.Count,5} types");
            units.Add(unit);
        }
        if (units.Count == 0) {
            log.WriteLine("error: no readable assemblies found");
            return 1;
        }

        var all = units.SelectMany(u => u.Types).ToList();
        // Cross-assembly type references are name-based, so bind them once here;
        // everything downstream works on resolved records.
        var index = new TypeIndex(all);
        index.BindAll(all);
        var selected = Selector.Select(all, options, log);

        if (options.List) {
            PrintList(selected, options, log, all.Count);
            foreach (var unit in units)
                unit.Dispose();
            return 0;
        }

        if (selected.Count == 0) {
            log.WriteLine("error: no types matched the selection");
            foreach (var unit in units)
                unit.Dispose();
            return 1;
        }

        var exitCode = 0;
        var report = options.ReportFile is not null ? new StreamWriter(options.ReportFile) : TextWriter.Null;
        try {
            var mapper = new TypeMapper(selected);
            var emitter = new HeaderEmitter(options, mapper, report);
            var header = emitter.Emit(selected);
            if (options.OutFile is not null) {
                var directory = Path.GetDirectoryName(Path.GetFullPath(options.OutFile));
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(options.OutFile, header, new UTF8Encoding(false));
                log.WriteLine($"wrote {options.OutFile} ({header.Length} bytes, {selected.Count} types)");
            } else {
                Console.Out.Write(header);
            }
        } catch (Exception ex) {
            log.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            exitCode = 1;
        } finally {
            report.Flush();
            if (options.ReportFile is not null)
                report.Dispose();
            foreach (var unit in units)
                unit.Dispose();
        }
        return exitCode;
    }

    private static void PrintList(List<TypeRecord> selected, Options options, TextWriter log, int totalLoaded) {
        foreach (var t in selected) {
            var kind = t.IsEnum ? "enum" : t.IsInterface ? "iface" : t.IsValueType ? "struct" : "class";
            var flags = t.IsNested ? " [nested]" : "";
            Console.WriteLine($"{kind,-6} {t.Image,-28} {(string.IsNullOrEmpty(t.Namespace) ? "(global)" : t.Namespace),-22} {t.Name}{flags}");
            if (!options.ListMembers)
                continue;
            foreach (var f in t.Fields)
                Console.WriteLine($"         field  {TypeMapper.ShortType(f.Type)} {f.Name}{(f.IsStatic ? " (static)" : "")}");
            foreach (var p in t.Properties)
                Console.WriteLine($"         prop   {TypeMapper.ShortType(p.Type)} {p.Name}{(p.IsStatic ? " (static)" : "")}");
            foreach (var m in t.Methods)
                Console.WriteLine($"         method {TypeMapper.ShortMethod(m)}{(m.IsStatic ? " (static)" : "")}");
            foreach (var (name, value) in t.EnumValues)
                Console.WriteLine($"         value  {name} = {value}");
        }
        log.WriteLine($"{selected.Count} type(s) selected from {totalLoaded} loaded.");
    }
}
