using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using Monolith.Core;

namespace Monolith.Modules.Games.Sources;

/// <summary>Primary play task from goggame-&lt;id&gt;.info.</summary>
public sealed record GogPlayTask(string Path, string? Arguments, string? WorkingDir);

/// <summary>
/// GOG Galaxy / GOG installers: HKLM\SOFTWARE\GOG.com\Games\&lt;gameId&gt; in the 32-bit view
/// (gameName, path, exe, dependsOn); fallback to &lt;path&gt;\goggame-&lt;id&gt;.info playTasks[isPrimary] (docs/research/tech.md §6).
/// Launch: the game exe directly.
/// </summary>
public static class GogSource
{
    public static GogPlayTask? ParsePrimaryPlayTask(string? infoJson)
    {
        if (string.IsNullOrWhiteSpace(infoJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(infoJson, new JsonDocumentOptions { AllowTrailingCommas = true });
            if (!doc.RootElement.TryGetProperty("playTasks", out var tasks) || tasks.ValueKind != JsonValueKind.Array) return null;

            JsonElement? pick = null;
            foreach (var t in tasks.EnumerateArray())
            {
                if (t.ValueKind != JsonValueKind.Object) continue;
                var type = t.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : "FileTask";
                if (!string.Equals(type, "FileTask", StringComparison.OrdinalIgnoreCase)) continue; // URL tasks (forums, manuals)
                var primary = t.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True;
                if (primary) { pick = t; break; }
                pick ??= t;
            }
            if (pick is not { } task) return null;
            var path = Get(task, "path");
            if (string.IsNullOrWhiteSpace(path)) return null;
            return new GogPlayTask(path.Replace('/', '\\'), Get(task, "arguments"), Get(task, "workingDir")?.Replace('/', '\\'));
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Get(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public static SourceResult Scan()
    {
        RegistryKey? games;
        try
        {
            using var hklm32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            games = hklm32.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        }
        catch (Exception ex)
        {
            return new SourceResult(GamePlatform.Gog, false, [], ex.Message);
        }
        if (games is null) return SourceResult.NotInstalled(GamePlatform.Gog);

        var list = new List<GameEntry>();
        using (games)
        {
            foreach (var id in games.GetSubKeyNames())
            {
                try
                {
                    using var k = games.OpenSubKey(id);
                    if (k is null) continue;
                    if (!string.IsNullOrWhiteSpace(k.GetValue("dependsOn")?.ToString())) continue; // DLC
                    var name = k.GetValue("gameName")?.ToString()?.Trim();
                    var dir = GamePaths.NormalizeDir(k.GetValue("path")?.ToString());
                    if (string.IsNullOrEmpty(name) || dir.Length == 0 || !Directory.Exists(dir)) continue;

                    string? exe = null, args = null, work = null;
                    var regExe = k.GetValue("exe")?.ToString()?.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(regExe))
                        exe = Path.IsPathRooted(regExe) ? regExe : Path.Combine(dir, regExe);
                    if (exe is null || !File.Exists(exe))
                    {
                        var gameId = k.GetValue("gameID")?.ToString() ?? id;
                        var info = Path.Combine(dir, $"goggame-{gameId}.info");
                        if (File.Exists(info) && ParsePrimaryPlayTask(File.ReadAllText(info)) is { } task)
                        {
                            exe = Path.IsPathRooted(task.Path) ? task.Path : Path.Combine(dir, task.Path);
                            args = task.Arguments;
                            work = string.IsNullOrEmpty(task.WorkingDir) ? null
                                : Path.IsPathRooted(task.WorkingDir) ? task.WorkingDir : Path.Combine(dir, task.WorkingDir);
                        }
                    }
                    if (exe is null || !File.Exists(exe)) continue;

                    list.Add(new GameEntry
                    {
                        Id = $"gog:{id}",
                        Name = name,
                        Platform = GamePlatform.Gog,
                        InstallDir = dir,
                        ExePath = Path.GetFullPath(exe),
                        Arguments = string.IsNullOrWhiteSpace(args) ? null : args,
                        WorkingDir = string.IsNullOrWhiteSpace(work) ? Path.GetDirectoryName(exe) : work,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn($"GOG: пропущена запись {id} — {ex.Message}");
                }
            }
        }
        return new SourceResult(GamePlatform.Gog, true, list);
    }
}
