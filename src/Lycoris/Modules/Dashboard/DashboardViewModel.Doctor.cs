using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lycoris.Core;
using Lycoris.Modules.Dashboard.Checks;
using Lycoris.Modules.Dashboard.Doctor;
using Lycoris.Modules.Dashboard.Monitoring;
using Lycoris.UI;

namespace Lycoris.Modules.Dashboard;

/// <summary>
/// «ПОЧЕМУ ТОРМОЗИТ?» (roadmap item 2, «Доктор ПК»): a 10-second read-only measurement — the live monitor's CPU/RAM/disk/GPU
/// samples plus each program's CPU time — explained by <see cref="PcDoctor"/>. Nothing is changed or closed.
/// </summary>
public sealed partial class DashboardViewModel
{
    private SystemSnapshot? _lastSnapshot;

    public ObservableCollection<CheckItemViewModel> DoctorItems { get; } = [];

    [ObservableProperty] private bool _isDiagnosing;
    [ObservableProperty] private bool _hasDoctor;
    [ObservableProperty] private string _doctorStatus = "";

    public string DoctorSourceUrl => PcDoctor.MicrosoftTipsUrl;

    [RelayCommand]
    private async Task DiagnoseAsync()
    {
        if (IsDiagnosing) return;
        IsDiagnosing = true;
        HasDoctor = true;
        DoctorItems.Clear();
        DoctorStatus = Loc.Tf("Замер нагрузки {0} с… Не закрывайте программы, которые сейчас тормозят.", (int)PcDoctor.Window.TotalSeconds);
        try
        {
            DoctorSample? sample = null;
            // Not RunBusyAsync: the page's own checks may be running, and a read-only measurement must not wait for them or be dropped.
            try
            {
                await OperationCoordinator.Instance.RunAsync(new OperationRequest(OperationClass.Measure, Loc.T("Замер: почему тормозит…")) { Owner = Key },
                    async _ =>
                    {
                        sample = await MeasureAsync(PcDoctor.Window);
                        _lastSnapshot ??= await SystemInspector.CollectAsync();
                    });
            }
            catch (Exception ex)
            {
                Log.Error("Замер «почему тормозит» не удался", ex);
            }
            if (sample is null || _lastSnapshot is null) { DoctorStatus = Loc.T("Замер не удался. Подробности — в журнале действий."); return; }
            foreach (var r in PcDoctor.Diagnose(sample, _lastSnapshot))
                DoctorItems.Add(new CheckItemViewModel(r, RunActionAsync, ActionLabel));
            DoctorStatus = Loc.Tf("Замер {0} с в {1}: процессор {2} %, память {3} %, диск {4} %.", (int)sample.Window.TotalSeconds, Loc.TimeText(DateTime.Now),
                DashFormat.Percent(sample.CpuAvg), DashFormat.Percent(sample.RamPercent), DashFormat.Percent(sample.DiskAvg));
        }
        finally
        {
            IsDiagnosing = false;
        }
    }

    [RelayCommand]
    private void CloseDoctor()
    {
        HasDoctor = false;
        DoctorItems.Clear();
    }

    [RelayCommand]
    private async Task OpenDoctorSourceAsync()
    {
        if (!await UnelevatedShell.OpenAsync(PcDoctor.MicrosoftTipsUrl)) Log.Warn("Не удалось открыть страницу: {0}", PcDoctor.MicrosoftTipsUrl);
    }

    /// <summary>Collects the live monitor's samples for the window and each process's CPU time at both ends of it.</summary>
    private async Task<DoctorSample> MeasureAsync(TimeSpan window)
    {
        var samples = new List<MonitorSample>();
        void OnSample(MonitorSample s) { lock (samples) samples.Add(s); }
        var startedMonitor = !_monitor.IsRunning;
        if (startedMonitor) _monitor.Start();
        _monitor.Sampled += OnSample;
        try
        {
            var before = await Task.Run(ReadProcessTimes);
            var clock = Stopwatch.StartNew();
            await Task.Delay(window);
            var after = await Task.Run(ReadProcessTimes);
            var elapsed = clock.Elapsed;

            var deltas = after.Select(a => before.TryGetValue(a.Key, out var b) && b.Name == a.Value.Name
                ? (a.Value.Name, a.Value.Cpu - b.Cpu, a.Value.Private)
                : (a.Value.Name, TimeSpan.Zero, a.Value.Private)).ToList();
            var programs = PcDoctor.Group(deltas, elapsed, Environment.ProcessorCount);

            MonitorSample[] got;
            lock (samples) got = [.. samples];
            return new DoctorSample(elapsed,
                Average(got.Select(s => s.CpuPercent)), got.LastOrDefault()?.RamPercent, Average(got.Select(s => s.DiskPercent)),
                got.Select(s => s.GpuTemperature).LastOrDefault(t => t is not null),
                programs.OrderByDescending(p => p.CpuPercent).Take(5).ToList(),
                programs.OrderByDescending(p => p.MemoryBytes).Take(5).ToList());
        }
        finally
        {
            _monitor.Sampled -= OnSample;
            if (startedMonitor) _monitor.Stop();
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var v = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    /// <summary>CPU time and memory in RAM (working set, as Task Manager shows) of every process this account may read (others are skipped without rights).</summary>
    private static Dictionary<int, (string Name, TimeSpan Cpu, long Private)> ReadProcessTimes()
    {
        var map = new Dictionary<int, (string, TimeSpan, long)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try { map[p.Id] = (p.ProcessName, p.TotalProcessorTime, p.WorkingSet64); }
                catch { /* access denied or exited: not counted */ }
            }
        }
        return map;
    }
}
