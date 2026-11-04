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
/// samples (at least one a second) plus each program's CPU time — explained by <see cref="PcDoctor"/> against a fresh system
/// snapshot. Nothing is changed or closed.
/// </summary>
public sealed partial class DashboardViewModel
{
    private SystemSnapshot? _lastSnapshot;

    /// <summary>The page itself shows the live tiles (visible, not minimized). The doctor keeps the monitor running past that.</summary>
    private bool _monitorWanted;

    public ObservableCollection<CheckItemViewModel> DoctorItems { get; } = [];

    [ObservableProperty] private bool _isDiagnosing;
    [ObservableProperty] private bool _hasDoctor;
    [ObservableProperty] private string _doctorStatus = "";

    public string DoctorSourceUrl => PcDoctor.MicrosoftTipsUrl;

    /// <summary>The page no longer needs the live tiles; a running measurement keeps the monitor until it ends.</summary>
    private void ReleaseMonitor()
    {
        _monitorWanted = false;
        if (!IsDiagnosing) _monitor.Stop();
    }

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
            SystemSnapshot? snapshot = null;
            // Not RunBusyAsync: the page's own checks may be running, and a read-only measurement must not wait for them or be dropped.
            try
            {
                await OperationCoordinator.Instance.RunAsync(new OperationRequest(OperationClass.Measure, Loc.T("Замер: почему тормозит…")) { Owner = Key },
                    async _ =>
                    {
                        sample = await MeasureAsync(PcDoctor.Window);
                        // Fresh facts after the window (free space, uptime, pending restart may have changed since the page's check);
                        // collected afterwards so its own WMI work is not counted as load.
                        snapshot = await SystemInspector.CollectAsync();
                    });
            }
            catch (Exception ex)
            {
                Log.Error("Замер «почему тормозит» не удался", ex);
            }
            if (snapshot is not null) _lastSnapshot = snapshot;
            if (sample is null || _lastSnapshot is null) { DoctorStatus = Loc.T("Замер не удался. Подробности — в журнале действий."); return; }
            foreach (var r in PcDoctor.Diagnose(sample, _lastSnapshot))
                DoctorItems.Add(new CheckItemViewModel(r, RunActionAsync, ActionLabel));
            DoctorStatus = sample.Samples < PcDoctor.MinSamples
                ? Loc.T("Нагрузка не измерена.")
                : Loc.Tf("Замер {0} с в {1}: процессор {2} %, память {3} %, диск {4} %.", (int)sample.Window.TotalSeconds, Loc.TimeText(DateTime.Now),
                    DashFormat.Percent(sample.CpuAvg), DashFormat.Percent(sample.RamPercent), DashFormat.Percent(sample.DiskAvg)) +
                  (sample.HiddenPrograms > 0 ? " " + Loc.T("Часть системных процессов без прав администратора не видна.") : "");
        }
        finally
        {
            IsDiagnosing = false;
            if (!_monitorWanted) _monitor.Stop();
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
        if (await UnelevatedShell.OpenAsync(PcDoctor.MicrosoftTipsUrl)) return;
        Log.Warn("Не удалось открыть страницу: {0}", PcDoctor.MicrosoftTipsUrl);
        Ui.Toast(Loc.Tf("Не удалось открыть {0}", "support.microsoft.com"), ToastKind.Warning);
    }

    /// <summary>Collects the live monitor's samples for the window and each process's CPU time at both ends of it.</summary>
    private async Task<DoctorSample> MeasureAsync(TimeSpan window)
    {
        var samples = new List<MonitorSample>();
        void OnSample(MonitorSample s) { lock (samples) samples.Add(s); }
        _monitor.MaxIntervalMs = 1000; // dense samples whatever the polling setting (up to 10 s) is
        _monitor.Sampled += OnSample;
        // Restart so the 1-second interval applies from the first second: a running loop may be mid-way through a 5-second wait.
        _monitor.Stop();
        _monitor.Start();
        try
        {
            var before = await Task.Run(ReadProcessTimes);
            var clock = Stopwatch.StartNew();
            await Task.Delay(window);
            var after = await Task.Run(ReadProcessTimes);
            var elapsed = clock.Elapsed;

            // CPU time only for processes readable at both ends; memory for every process whose memory is readable.
            var deltas = after.Select(a => (a.Value.Name,
                    a.Value.Cpu is { } now && before.TryGetValue(a.Key, out var b) && b.Name == a.Value.Name && b.Cpu is { } then ? now - then : TimeSpan.Zero,
                    a.Value.Memory ?? 0)).ToList();
            var programs = PcDoctor.Group(deltas, elapsed, Environment.ProcessorCount);
            var hidden = after.Count(a => a.Value.Cpu is null);

            MonitorSample[] got;
            lock (samples) got = [.. samples];
            return new DoctorSample(elapsed,
                Average(got.Select(s => s.CpuPercent)), got.LastOrDefault()?.RamPercent, Average(got.Select(s => s.DiskPercent)),
                got.Select(s => s.GpuTemperature).LastOrDefault(t => t is not null),
                programs.OrderByDescending(p => p.CpuPercent).Take(5).ToList(),
                programs.OrderByDescending(p => p.MemoryBytes).Take(5).ToList(),
                got.Length, hidden);
        }
        finally
        {
            _monitor.Sampled -= OnSample;
            _monitor.MaxIntervalMs = int.MaxValue;
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var v = values.Where(x => x is not null).Select(x => x!.Value).ToList();
        return v.Count == 0 ? null : v.Average();
    }

    /// <summary>
    /// CPU time and memory in RAM (working set, as Task Manager shows) of every process. Without admin rights the CPU time of
    /// system services cannot be read (null), while their memory usually can — each is read on its own.
    /// </summary>
    private static Dictionary<int, (string Name, TimeSpan? Cpu, long? Memory)> ReadProcessTimes()
    {
        var map = new Dictionary<int, (string, TimeSpan?, long?)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                if (p.Id == 0 || p.Id == Environment.ProcessId) continue; // System Idle Process; Lycoris itself — by id, never by name
                string name;
                try { name = p.ProcessName; } catch { continue; } // exited
                TimeSpan? cpu = null;
                long? memory = null;
                try { cpu = p.TotalProcessorTime; } catch { /* access denied */ }
                try { memory = p.WorkingSet64; } catch { /* access denied or exited */ }
                map[p.Id] = (name, cpu, memory);
            }
        }
        return map;
    }
}
