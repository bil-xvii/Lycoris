using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Lycoris.Core.Security;

namespace Lycoris.Core.Background;

public enum BackgroundStartupState { Absent, Enabled, Disabled }

/// <summary>Undo can recreate only this app at the same location. The journal supplies neither an executable path nor XML.</summary>
public sealed record BackgroundStartupSnapshot(BackgroundStartupState State, string ExecutableHash)
{
    internal bool IsValid => Enum.IsDefined(State) && ExecutableHash is { Length: 64 }
        && ExecutableHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal interface IBackgroundStartupStore
{
    string? ReadXml(string taskPath);
    void Create(string taskPath, string xml);
    void SetEnabled(string taskPath, bool enabled);
    void Delete(string taskPath);
}

/// <summary>The per-account Task Scheduler registration. Mutations are called by ChangeSession and journal undo.</summary>
internal sealed class BackgroundStartupRegistration
{
    internal const string Source = "background-startup";
    private const string Prefix = "Lycoris Logon ";
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private readonly IBackgroundStartupStore _store;

    private BackgroundStartupRegistration(string sid, string executable, IBackgroundStartupStore store)
    {
        Sid = sid;
        Executable = executable;
        _store = store;
    }

    public string Sid { get; }
    public string Executable { get; }
    private string TaskPath => PathFor(Sid);

    internal static string PathFor(string sid) => "\\" + Prefix + sid;
    internal static string TargetFor(string sid) => Source + ":" + sid;
    internal static bool IsTarget(string? target) => target?.StartsWith(Source + ":", StringComparison.Ordinal) == true;
    internal static bool IsReservedTask(string? path) => path?.Split(['\\', '/']).LastOrDefault()
        ?.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    public static BackgroundStartupRegistration ForInteractiveUser()
    {
        var interactive = Elevation.InteractiveUserState;
        if (interactive != true)
            throw new InvalidOperationException(interactive == false ? Elevation.OtherAccountText : Elevation.UnknownAccountText);
        return TryCreate(UserScope.CurrentSid, Environment.ProcessPath)
            ?? throw new InvalidOperationException(Loc.T("Автозапуск доступен для Lycoris.exe в обычной локальной папке."));
    }

    internal static BackgroundStartupRegistration? TryCreate(string? sid, string? executable, IBackgroundStartupStore? store = null)
    {
        if (sid is null || !UserScope.SidPattern.IsMatch(sid) || executable is not { Length: >= 4 and <= 259 }
            || !char.IsAsciiLetter(executable[0]) || executable[1..3] != @":\" || executable[3..].Contains(':')
            || executable.Any(c => char.IsControl(c) || c is '"' or '%' or '/' or '<' or '>' or '|' or '*' or '?')) return null;
        try
        {
            if (!string.Equals(Path.GetFullPath(executable), executable, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(Path.GetFileName(executable), "Lycoris.exe", StringComparison.OrdinalIgnoreCase)) return null;
            return new BackgroundStartupRegistration(sid, executable, store ?? new SchedulerStore());
        }
        catch (ArgumentException) { return null; }
    }

    internal BackgroundStartupSnapshot Snapshot(BackgroundStartupState state) =>
        new(state, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Executable.ToUpperInvariant()))));

    public BackgroundStartupState ReadState()
    {
        if (_store.ReadXml(TaskPath) is not { } xml) return BackgroundStartupState.Absent;
        return ParseDefinition(xml) ?? throw new InvalidOperationException(
            Loc.T("Одноимённая задача отличается от задачи Lycoris или указывает на другую копию программы. Проверьте её в планировщике заданий Windows."));
    }

    internal void Apply(BackgroundStartupState expected, BackgroundStartupState desired)
    {
        SafetyLock.ThrowIfLocked(Loc.T("изменение задачи планировщика"));
        if (ReadState() != expected)
            throw new InvalidOperationException(Loc.T("Задача автозапуска изменилась. Обновите её состояние и повторите действие."));
        if (expected == desired) return;
        if (desired == BackgroundStartupState.Absent) _store.Delete(TaskPath);
        else if (expected == BackgroundStartupState.Absent) _store.Create(TaskPath, BuildDefinition(desired));
        else _store.SetEnabled(TaskPath, desired == BackgroundStartupState.Enabled);

        if (ReadState() != desired)
            throw new InvalidOperationException(Loc.T("Планировщик заданий не подтвердил новое состояние автозапуска."));
    }

    internal void Restore(string sid, BackgroundStartupSnapshot original)
    {
        if (!UserScope.Same(Sid, sid) || !original.IsValid || original != Snapshot(original.State))
            throw new InvalidOperationException(Loc.T("Запись автозапуска относится к другой учётной записи или другой папке Lycoris."));
        Apply(ReadState(), original.State);
    }

    internal string BuildDefinition(BackgroundStartupState state) => Definition(state).ToString(SaveOptions.DisableFormatting);

    private XElement Definition(BackgroundStartupState state)
    {
        if (state is not (BackgroundStartupState.Enabled or BackgroundStartupState.Disabled))
            throw new ArgumentOutOfRangeException(nameof(state));
        return new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo", new XElement(Ns + "Author", "Lycoris")),
            new XElement(Ns + "Triggers", new XElement(Ns + "LogonTrigger", new XElement(Ns + "UserId", Sid))),
            new XElement(Ns + "Principals", new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                new XElement(Ns + "UserId", Sid), new XElement(Ns + "LogonType", "InteractiveToken"),
                new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings", new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", false), new XElement(Ns + "StopIfGoingOnBatteries", false),
                new XElement(Ns + "Enabled", state == BackgroundStartupState.Enabled),
                new XElement(Ns + "ExecutionTimeLimit", "PT0S")),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"), new XElement(Ns + "Exec",
                new XElement(Ns + "Command", Executable), new XElement(Ns + "Arguments", BackgroundModePolicy.TaskArguments))));
    }

    internal BackgroundStartupState? ParseDefinition(string xml)
    {
        if (xml.Length > 64 * 1024) return null;
        try
        {
            if (SafeXml.Parse(xml).Root is not { } actual || actual.Name != Ns + "Task"
                || (string?)actual.Attribute("version") is not ("1.2" or "1.3" or "1.4")) return null;
            var enabled = actual.Element(Ns + "Settings")?.Element(Ns + "Enabled");
            var state = enabled is { HasElements: false, Value: "false" } ? BackgroundStartupState.Disabled : BackgroundStartupState.Enabled;
            var expected = Definition(state);
            Normalize(actual);
            Normalize(expected);
            return XNode.DeepEquals(Sort(actual, 0), Sort(expected, 0)) ? state : null;
        }
        catch (XmlException) { return null; }
    }

    // The scheduler may reorder settings and materialize defaults. Any non-default additions, extra triggers, altered
    // principal, working directory or arguments make this a foreign task; its XML is never used to register anything.
    private void Normalize(XElement task)
    {
        task.Attribute("version")?.Remove();
        DropDefault(task.Element(Ns + "RegistrationInfo"), "URI", TaskPath);
        DropDefault(task.Element(Ns + "Triggers")?.Element(Ns + "LogonTrigger"), "Enabled", "true");
        DropDefault(task.Element(Ns + "Principals")?.Element(Ns + "Principal"), "RunLevel", "LeastPrivilege");
        var settings = task.Element(Ns + "Settings");
        DropDefault(settings, "MultipleInstancesPolicy", "IgnoreNew");
        DropDefault(settings, "Enabled", "true");
        foreach (var flag in new[] { "StartWhenAvailable", "RunOnlyIfNetworkAvailable", "RunOnlyIfIdle", "Hidden",
                     "WakeToRun", "DisallowStartOnRemoteAppSession", "UseUnifiedSchedulingEngine", "Volatile" })
            DropDefault(settings, flag, "false");
        DropDefault(settings, "AllowStartOnDemand", "true");
        DropDefault(settings, "AllowHardTerminate", "true");
        DropDefault(settings, "Priority", "7");

        if (settings?.Element(Ns + "IdleSettings") is { } idle)
        {
            DropDefault(idle, "Duration", "PT10M");
            DropDefault(idle, "WaitTimeout", "PT1H");
            DropDefault(idle, "StopOnIdleEnd", "true");
            DropDefault(idle, "RestartOnIdle", "false");
            if (!idle.HasAttributes && !idle.HasElements && string.IsNullOrWhiteSpace(idle.Value)) idle.Remove();
        }

        var command = task.Element(Ns + "Actions")?.Element(Ns + "Exec")?.Element(Ns + "Command");
        if (command is { HasElements: false } && (string.Equals(command.Value, Executable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(command.Value, '"' + Executable + '"', StringComparison.OrdinalIgnoreCase)))
            command.Value = Executable;
    }

    private static void DropDefault(XElement? parent, string name, string value)
    {
        var entries = parent?.Elements(Ns + name).ToArray();
        if (entries is [{ HasAttributes: false, HasElements: false } entry] && entry.Value == value) entry.Remove();
    }

    private static XElement Sort(XElement e, int depth)
    {
        if (depth > 8) throw new XmlException();
        return new XElement(e.Name,
            e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal),
            e.Nodes().Where(n => n is not XElement and not XComment && (n is not XText t || !string.IsNullOrWhiteSpace(t.Value))),
            e.Elements().OrderBy(x => x.Name.ToString(), StringComparer.Ordinal).Select(x => Sort(x, depth + 1)));
    }

    internal static string StateText(BackgroundStartupState state) => state switch
    {
        BackgroundStartupState.Enabled => Loc.Keep("Включена"),
        BackgroundStartupState.Disabled => Loc.Keep("Отключена"),
        _ => Loc.Keep("Нет задачи"),
    };

    private sealed class SchedulerStore : IBackgroundStartupStore
    {
        public string? ReadXml(string taskPath) => ScheduledTasks.ReadXml(taskPath);

        public void Create(string taskPath, string xml)
        {
            var verdict = SelfIntegrity.CheckBeforeElevation();
            if (!verdict.Allowed) throw new InvalidOperationException(verdict.Reason);
            ScheduledTasks.RegisterXml(taskPath, xml, createOnly: true);
        }

        public void SetEnabled(string taskPath, bool enabled) => ScheduledTasks.SetEnabled(taskPath, enabled);
        public void Delete(string taskPath) => ScheduledTasks.Delete(taskPath);
    }
}
