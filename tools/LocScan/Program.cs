using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Monolith.LocScan;

// MONOLITH localization tool. Wrapper: tools/loc-scan.ps1.  Run `LocScan help` for the commands.
var repo = FindRepo();
var src = Path.Combine(repo, "src", "Monolith");
var enDir = Path.Combine(src, "Core", "Localization", "en");
var stateDir = Path.Combine(repo, "obj", "loc");

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
return cmd switch
{
    "report" => Report(),
    "check" => Check(args.Length > 1 ? args[1] : "--all"),
    "missing" => Missing(Need(1, "area")),
    "fill" => Fill(Need(1, "area"), Need(2, "json file")),
    "new" => New(Need(1, "area"), Need(2, "sources (comma-separated globs relative to src/Monolith)")),
    _ => Help(),
};

string Need(int i, string what)
{
    if (args.Length > i) return args[i];
    Console.Error.WriteLine($"missing argument: {what}");
    Environment.Exit(2);
    return "";
}

static string FindRepo()
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Monolith.slnx"))) dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("Monolith.slnx not found above the current directory");
}

int Help()
{
    Console.WriteLine("""
        LocScan — MONOLITH localization progress and helper (docs/localization.md)

          report                    per module: Cyrillic string literals that still need Loc (and how many are already keyed)
          check [Area|--all]        violations of a translated area (or of every area with "translated": true); exit code = count
          missing <Area>            numbered list of the Russian keys used in the area's sources that have no English entry yet
          fill <Area> <file.json>   merge {"<number>": "English", …} (numbers from the last `missing <Area>`) into the area file
          new <Area> <globs>        create Core/Localization/en/<Area>.json for sources like "Modules/Optimize/**,Tweaks/*"
        """);
    return 0;
}

// ───────────────────────────── Areas ─────────────────────────────

List<(string Name, string Path, JsonObject Json)> LoadAreaFiles() =>
    Directory.Exists(enDir)
        ? Directory.EnumerateFiles(enDir, "*.json").OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => (System.IO.Path.GetFileNameWithoutExtension(f), f, (JsonObject)JsonNode.Parse(File.ReadAllText(f), null, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!))
            .ToList()
        : [];

HashSet<string> AllKeys() => LoadAreaFiles().SelectMany(a => (a.Json["en"] as JsonObject)?.Select(kv => kv.Key) ?? []).ToHashSet(StringComparer.Ordinal);

static List<string> Strings(JsonObject o, string name) => (o[name] as JsonArray)?.Select(x => x?.GetValue<string>() ?? "").ToList() ?? [];

(IReadOnlyList<string> Files, HashSet<string> Ignore) AreaSources((string Name, string Path, JsonObject Json) a) =>
    (Scanner.ExpandSources(src, Strings(a.Json, "sources")), Strings(a.Json, "ignore").ToHashSet(StringComparer.Ordinal));

// ───────────────────────────── Commands ─────────────────────────────

int Report()
{
    var areas = LoadAreaFiles();
    var keys = AllKeys();
    var translated = new HashSet<string>(areas.Where(a => a.Json["translated"]?.GetValue<bool>() == true).SelectMany(a => AreaSources(a).Files), StringComparer.Ordinal);
    var inArea = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var a in areas) foreach (var f in AreaSources(a).Files) inArea.TryAdd(f, a.Name);

    var rows = new SortedDictionary<string, (int Files, int Need, int Keyed, int Missing, bool Done)>(StringComparer.Ordinal);
    foreach (var f in Scanner.ExpandSources(src, ["**"]))
    {
        if (f.StartsWith("Core/Localization/", StringComparison.Ordinal)) continue;
        var ignore = inArea.TryGetValue(f, out var an) ? AreaSources(areas.First(x => x.Name == an)).Ignore : null;
        var found = Scanner.ScanFile(Path.Combine(src, f), f, ignore);
        var need = found.Count(x => x.Kind == FindingKind.Violation);
        var keyed = found.Count(x => x.Kind == FindingKind.Key && keys.Contains(x.Text));
        var missing = found.Count(x => x.Kind == FindingKind.Key && !keys.Contains(x.Text));
        if (need + keyed + missing == 0) continue;
        var module = ModuleOf(f);
        rows.TryGetValue(module, out var r);
        rows[module] = (r.Files + 1, r.Need + need, r.Keyed + keyed, r.Missing + missing, r.Done || false);
    }
    Console.WriteLine($"{"Module",-28}{"files",6}{"to do",8}{"keyed",8}{"no EN",8}   state");
    int totalNeed = 0, totalKeyed = 0, totalMissing = 0;
    foreach (var (module, r) in rows)
    {
        var state = r.Need + r.Missing == 0 ? "done" : r.Keyed > 0 ? "in progress" : "-";
        Console.WriteLine($"{module,-28}{r.Files,6}{r.Need,8}{r.Keyed,8}{r.Missing,8}   {state}");
        totalNeed += r.Need; totalKeyed += r.Keyed; totalMissing += r.Missing;
    }
    Console.WriteLine($"{"TOTAL",-28}{rows.Values.Sum(x => x.Files),6}{totalNeed,8}{totalKeyed,8}{totalMissing,8}");
    Console.WriteLine();
    Console.WriteLine("to do = Cyrillic literal not wrapped in Loc / a sink · keyed = wrapped and has an English entry · no EN = wrapped, entry missing");
    Console.WriteLine("Translated areas: " + (areas.Where(a => a.Json["translated"]?.GetValue<bool>() == true).Select(a => a.Name).DefaultIfEmpty("(none)").Aggregate((x, y) => x + ", " + y)));
    return 0;
}

static string ModuleOf(string rel)
{
    var p = rel.Split('/');
    if (p[0] == "Modules" && p.Length > 2) return p[0] + "/" + p[1];
    if (p.Length > 1) return p[0];
    return "(root)";
}

int Check(string which)
{
    var areas = LoadAreaFiles();
    var keys = AllKeys();
    var chosen = which == "--all"
        ? areas.Where(a => a.Json["translated"]?.GetValue<bool>() == true).ToList()
        : areas.Where(a => a.Name.Equals(which, StringComparison.OrdinalIgnoreCase)).ToList();
    if (chosen.Count == 0) { Console.Error.WriteLine("no such area / no translated areas"); return which == "--all" ? 0 : 2; }
    var bad = 0;
    foreach (var a in chosen)
    {
        var (files, ignore) = AreaSources(a);
        foreach (var f in files)
            foreach (var x in Scanner.ScanFile(Path.Combine(src, f), f, ignore))
            {
                if (x.Kind == FindingKind.Violation) { Console.WriteLine($"{a.Name}: {x.File}:{x.Line}: {x.Why}: «{Short(x.Text)}»"); bad++; }
                else if (!keys.Contains(x.Text)) { Console.WriteLine($"{a.Name}: {x.File}:{x.Line}: no English entry: «{Short(x.Text)}»"); bad++; }
            }
    }
    Console.WriteLine(bad == 0 ? "OK" : $"{bad} problem(s)");
    return bad;
}

static string Short(string s) => (s.Length > 90 ? s[..90] + "…" : s).Replace("\n", "\\n");

int Missing(string areaName)
{
    var area = LoadAreaFiles().FirstOrDefault(a => a.Name.Equals(areaName, StringComparison.OrdinalIgnoreCase));
    if (area.Json is null) { Console.Error.WriteLine("no such area; create it with `new`"); return 2; }
    var keys = AllKeys();
    var (files, ignore) = AreaSources(area);
    var list = new List<string>();
    foreach (var f in files)
        foreach (var x in Scanner.ScanFile(Path.Combine(src, f), f, ignore))
            if (x.Kind == FindingKind.Key && !keys.Contains(x.Text) && !list.Contains(x.Text)) list.Add(x.Text);
    Directory.CreateDirectory(stateDir);
    File.WriteAllText(Path.Combine(stateDir, area.Name + ".missing.json"), JsonSerializer.Serialize(list, Opts.Json));
    for (var i = 0; i < list.Count; i++) Console.WriteLine($"{i + 1}\t{list[i].Replace("\n", "\\n")}");
    Console.WriteLine($"-- {list.Count} key(s) without an English entry (numbers are valid for `fill {area.Name}`)");
    return 0;
}

int Fill(string areaName, string jsonFile)
{
    var area = LoadAreaFiles().FirstOrDefault(a => a.Name.Equals(areaName, StringComparison.OrdinalIgnoreCase));
    if (area.Json is null) { Console.Error.WriteLine("no such area"); return 2; }
    var state = Path.Combine(stateDir, area.Name + ".missing.json");
    if (!File.Exists(state)) { Console.Error.WriteLine("run `missing` first"); return 2; }
    var keys = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(state))!;
    var give = JsonNode.Parse(File.ReadAllText(jsonFile))!.AsObject();
    var en = area.Json["en"] as JsonObject ?? new JsonObject();
    area.Json["en"] = en;
    var added = 0;
    foreach (var (num, value) in give)
    {
        if (!int.TryParse(num, out var n) || n < 1 || n > keys.Count) { Console.Error.WriteLine($"bad number {num}"); return 2; }
        en[keys[n - 1]] = value!.GetValue<string>();
        added++;
    }
    File.WriteAllText(area.Path, area.Json.ToJsonString(Opts.Json) + "\n");
    Console.WriteLine($"added {added} entr{(added == 1 ? "y" : "ies")} to {Path.GetFileName(area.Path)}");
    return 0;
}

int New(string name, string globs)
{
    Directory.CreateDirectory(enDir);
    var path = Path.Combine(enDir, name + ".json");
    if (File.Exists(path)) { Console.Error.WriteLine("already exists"); return 2; }
    var o = new JsonObject
    {
        ["area"] = name,
        ["translated"] = false,
        ["sources"] = new JsonArray(globs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(g => (JsonNode)g).ToArray()),
        ["ignore"] = new JsonArray(),
        ["en"] = new JsonObject(),
    };
    File.WriteAllText(path, o.ToJsonString(Opts.Json) + "\n");
    Console.WriteLine("created " + path);
    return 0;
}

static class Opts
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
}
