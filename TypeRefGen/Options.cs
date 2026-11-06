namespace TypeRefGen;

internal sealed class Options {
    public string DllDir = "";
    public string? OutFile;
    public string Namespace = "GameTypes";
    public List<string> Assemblies = new();
    public List<string> Types = new();
    public List<string> Matches = new();
    public List<string> Excludes = new();
    public bool List;
    public bool ListMembers;
    public int Max;
    public int MaxMembers;
    public bool Fields = true;
    public bool Properties = true;
    public bool Methods;
    public bool ExactMethods;
    public bool FindHelpers = true;
    public bool SelfCheck = true;
    public bool IncludeNested;
    public bool IncludeGenerics;
    public bool IncludeNonPublic;
    public bool IncludeValueTypes;
    public bool AllAssemblies;
    public bool Verbose;
    public string? ReportFile;

    public const string DefaultAssembly = "Assembly-CSharp";

    public void ApplyDefaults() {
        if (!AllAssemblies && Assemblies.Count == 0)
            Assemblies.Add(DefaultAssembly);
    }
}
