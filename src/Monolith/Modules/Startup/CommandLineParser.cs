using System.IO;

namespace Monolith.Modules.Startup;

/// <summary>A startup command split into the program that Windows starts and the file that identifies it.</summary>
/// <param name="Executable">The program Windows launches (expanded, resolved through the search path when possible).</param>
/// <param name="Arguments">Everything after the program.</param>
/// <param name="Target">The file that really identifies the entry: the executable, or the DLL / script a host
/// (rundll32, wscript, cscript) is asked to run. Used for the publisher, "file missing" and "open location".</param>
/// <param name="ViaHost">True when <paramref name="Target"/> was taken from the arguments of a host program.</param>
public sealed record CommandTarget(string Executable, string Arguments, string Target, bool ViaHost);

/// <summary>
/// Turns a Run-key / scheduled-task command line into the file it starts, following CreateProcess rules:
/// quoted program paths, unquoted paths with spaces (shortest existing prefix wins, like CreateProcess),
/// environment variables, missing ".exe", bare names found via System32 / Windows / PATH,
/// and host programs (rundll32 DLL,Entry; wscript/cscript script).
/// </summary>
public static class CommandLineParser
{
    private static readonly string[] ExecutableExtensions = [".exe", ".com", ".bat", ".cmd", ".scr", ".pif"];
    private static readonly string[] LibraryExtensions = [".dll", ".ocx", ".cpl", ".drv"];

    /// <param name="command">Raw command (as stored in the registry or task action).</param>
    /// <param name="fileExists">File check (defaults to the real file system).</param>
    /// <param name="expand">Environment-variable expansion (defaults to <see cref="Environment.ExpandEnvironmentVariables(string)"/>).</param>
    /// <param name="searchDirs">Folders for bare names (defaults to System32, Windows, then PATH).</param>
    public static CommandTarget? Parse(string? command, Func<string, bool>? fileExists = null,
        Func<string, string>? expand = null, IReadOnlyList<string>? searchDirs = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        fileExists ??= FileProbe.FileExists;
        expand ??= Environment.ExpandEnvironmentVariables;
        searchDirs ??= DefaultSearchDirs();

        var text = expand(command.Trim()).Trim();
        if (text.Length == 0) return null;

        var (program, args) = SplitProgram(text, fileExists, searchDirs);
        if (program.Length == 0) return null;

        var executable = Resolve(program, ".exe", fileExists, searchDirs) ?? program;
        var hostName = Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();

        string? hosted = hostName switch
        {
            "rundll32" => ExtractRundllTarget(args),
            "wscript" or "cscript" => ExtractScriptTarget(args),
            _ => null,
        };

        if (hosted is null)
            return new CommandTarget(executable, args, executable, false);

        var defaultExt = hostName == "rundll32" ? ".dll" : "";
        var target = Resolve(hosted, defaultExt, fileExists, searchDirs) ?? hosted;
        return new CommandTarget(executable, args, target, true);
    }

    /// <summary>Program part + arguments of a command line.</summary>
    internal static (string Program, string Arguments) SplitProgram(string text, Func<string, bool> fileExists, IReadOnlyList<string> searchDirs)
    {
        text = text.Trim();
        if (text.Length == 0) return ("", "");

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end < 0
                ? (text[1..].Trim(), "")
                : (text[1..end].Trim(), text[(end + 1)..].Trim());
        }

        var cuts = new List<int>();
        for (var i = 0; i < text.Length; i++)
            if (text[i] == ' ' || text[i] == '\t') cuts.Add(i);
        cuts.Add(text.Length);

        // 1. CreateProcess semantics: the shortest space-delimited prefix that names an existing file.
        if (cuts.Count > 1)
        {
            foreach (var cut in cuts)
            {
                var candidate = text[..cut].TrimEnd();
                if (candidate.Length > 0 && Resolve(candidate, ".exe", fileExists, searchDirs, mustExist: true) is not null)
                    return (candidate, text[cut..].Trim());
            }

            // 2. Nothing exists (deleted program): the first prefix that ends with an executable extension.
            foreach (var cut in cuts)
            {
                var candidate = text[..cut].TrimEnd();
                if (ExecutableExtensions.Any(e => candidate.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                    return (candidate, text[cut..].Trim());
            }
        }

        // 3. Plain first token.
        return (text[..cuts[0]], text[cuts[0]..].Trim());
    }

    /// <summary>
    /// Full path of <paramref name="name"/>: rooted names are returned as is (with <paramref name="defaultExt"/> appended
    /// when they have no extension), bare names are looked up in <paramref name="searchDirs"/>.
    /// With <paramref name="mustExist"/> only existing files are returned.
    /// </summary>
    internal static string? Resolve(string name, string defaultExt, Func<string, bool> fileExists, IReadOnlyList<string> searchDirs,
        bool mustExist = false)
    {
        name = name.Trim().Trim('"').Trim();
        if (name.Length == 0) return null;
        name = name.Replace('/', '\\');

        var appended = defaultExt.Length > 0 && !HasExtension(name);
        var withExt = appended ? name + defaultExt : name;

        if (IsRooted(name))
        {
            if (fileExists(withExt)) return withExt;
            if (appended && fileExists(name)) return name;
            return mustExist ? null : withExt;
        }

        // Relative path with folders ("bin\tool.exe") cannot be resolved reliably — the working directory is unknown.
        if (name.Contains('\\'))
            return mustExist ? null : withExt;

        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var full = Path.Join(dir.Trim(), withExt);
            if (fileExists(full)) return full;
        }
        return mustExist ? null : withExt;
    }

    /// <summary><c>rundll32 [/d] "C:\x\lib.dll",Entry args</c> → <c>C:\x\lib.dll</c>.</summary>
    internal static string? ExtractRundllTarget(string args)
    {
        var s = SkipSwitches(args, '/');
        if (s.Length == 0) return null;

        string dll;
        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            dll = end < 0 ? s[1..] : s[1..end];
        }
        else
        {
            var comma = s.IndexOf(',');
            var space = s.IndexOfAny([' ', '\t']);
            int cut;
            if (comma < 0) cut = space < 0 ? s.Length : space;
            else if (space >= 0 && space < comma && LibraryExtensions.Any(e => s[..space].EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                cut = space; // "lib.dll Entry a,b"
            else cut = comma;
            dll = s[..cut];
        }

        dll = dll.Trim().TrimEnd(',').Trim();
        return dll.Length > 0 ? dll : null;
    }

    /// <summary><c>wscript //B "C:\x\run.vbs" args</c> → <c>C:\x\run.vbs</c>.</summary>
    internal static string? ExtractScriptTarget(string args)
    {
        var s = args.Trim();
        while (s.StartsWith("//", StringComparison.Ordinal))
        {
            var sp = s.IndexOfAny([' ', '\t']);
            s = sp < 0 ? "" : s[sp..].TrimStart();
        }
        if (s.Length == 0) return null;

        if (s[0] == '"')
        {
            var end = s.IndexOf('"', 1);
            var quoted = end < 0 ? s[1..] : s[1..end];
            return quoted.Trim().Length > 0 ? quoted.Trim() : null;
        }

        var space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? s : s[..space];
    }

    /// <summary>Quotes a program path for display / re-parsing when it contains spaces.</summary>
    public static string Join(string program, string? arguments)
    {
        program = program.Trim();
        if (program.Contains(' ') && !program.StartsWith('"')) program = $"\"{program}\"";
        return string.IsNullOrWhiteSpace(arguments) ? program : $"{program} {arguments.Trim()}";
    }

    private static string SkipSwitches(string args, char prefix)
    {
        var s = args.Trim();
        while (s.Length > 0 && s[0] == prefix)
        {
            var sp = s.IndexOfAny([' ', '\t']);
            s = sp < 0 ? "" : s[sp..].TrimStart();
        }
        return s;
    }

    private static bool HasExtension(string name)
    {
        var file = name[(name.LastIndexOf('\\') + 1)..];
        var dot = file.LastIndexOf('.');
        return dot > 0 && dot < file.Length - 1;
    }

    private static bool IsRooted(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\');

    private static IReadOnlyList<string>? _defaultDirs;

    internal static IReadOnlyList<string> DefaultSearchDirs()
    {
        if (_defaultDirs is not null) return _defaultDirs;
        var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var expanded = Environment.ExpandEnvironmentVariables(p);
            if (Path.IsPathFullyQualified(expanded) && !dirs.Contains(expanded, StringComparer.OrdinalIgnoreCase))
                dirs.Add(expanded);
        }
        return _defaultDirs = dirs;
    }
}
