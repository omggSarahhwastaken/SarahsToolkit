using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace SarahsToolkit.Services
{
    // Vendor-neutral live system vitals, sampled once per second.
    // CPU/RAM/disk/network come from PerformanceCounter (works on AMD,
    // Intel and NVIDIA machines alike); GPU uses the "GPU Engine" counter
    // category that Windows maintains per 3D engine, so it is also
    // vendor-neutral. Anything unavailable on a machine degrades to NaN
    // and the UI hides that vital instead of showing garbage.
    // All counter reads happen on a background thread; the UI thread only
    // reads the latest snapshot and the history buffers.
    public sealed class VitalsService : IDisposable
    {
        public const int HistoryLength = 60;

        private readonly object _gate = new object();
        private bool _sampling;
        private bool _initialized;
        private bool _disposed;

        private PerformanceCounter _cpu;
        private PerformanceCounter _ram;
        private PerformanceCounter _disk;
        private List<PerformanceCounter> _net;
        private List<PerformanceCounter> _gpuEngines;
        private int _gpuRefreshCountdown;
        private readonly Ping _ping = new Ping();
        // Samples run every second; ping at most every 30th sample so the
        // dashboard can't cause ping spikes while gaming.
        private const int PingEverySamples = 30;
        private int _pingCountdown = 1; // ping on the very first sample

        public double Cpu { get; private set; } = double.NaN;
        public double Gpu { get; private set; } = double.NaN;
        public double Ram { get; private set; } = double.NaN;
        public double Disk { get; private set; } = double.NaN;
        public double NetMbps { get; private set; } = double.NaN;
        public double PingMs { get; private set; } = double.NaN;

        public bool HasGpu { get; private set; }

        private readonly VitalHistory _cpuHist = new VitalHistory(HistoryLength);
        private readonly VitalHistory _gpuHist = new VitalHistory(HistoryLength);
        private readonly VitalHistory _ramHist = new VitalHistory(HistoryLength);
        private readonly VitalHistory _diskHist = new VitalHistory(HistoryLength);
        private readonly VitalHistory _netHist = new VitalHistory(HistoryLength);
        private readonly VitalHistory _pingHist = new VitalHistory(HistoryLength);

        public double[] CpuHistory => _cpuHist.Snapshot();
        public double[] GpuHistory => _gpuHist.Snapshot();
        public double[] RamHistory => _ramHist.Snapshot();
        public double[] DiskHistory => _diskHist.Snapshot();
        public double[] NetHistory => _netHist.Snapshot();
        public double[] PingHistory => _pingHist.Snapshot();

        private void EnsureCounters()
        {
            if (_initialized) return;
            _initialized = true;
            try
            {
                _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
                _cpu.NextValue(); // warm up; first read of an interval counter is always 0
            }
            catch { _cpu = null; }
            try
            {
                _ram = new PerformanceCounter("Memory", "% Committed Bytes In Use", true);
            }
            catch { _ram = null; }
            try
            {
                _disk = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                _disk.NextValue();
            }
            catch { _disk = null; }
            try
            {
                var cat = new PerformanceCounterCategory("Network Interface");
                _net = new List<PerformanceCounter>();
                foreach (var name in cat.GetInstanceNames())
                {
                    try
                    {
                        var c = new PerformanceCounter("Network Interface", "Bytes Total/sec", name, true);
                        c.NextValue();
                        _net.Add(c);
                    }
                    catch { /* skip this interface */ }
                }
                if (_net.Count == 0) _net = null;
            }
            catch { _net = null; }
            RefreshGpuEngines();
        }

        private void RefreshGpuEngines()
        {
            try
            {
                if (_gpuEngines != null)
                    foreach (var c in _gpuEngines) { try { c.Dispose(); } catch { } }
                _gpuEngines = new List<PerformanceCounter>();
                var cat = new PerformanceCounterCategory("GPU Engine");
                foreach (var name in cat.GetInstanceNames())
                {
                    // Instance names look like pid_1234_luid_0x..._eng_0_engtype_3D.
                    // Summing the 3D engines is what Task Manager does.
                    if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { _gpuEngines.Add(new PerformanceCounter("GPU Engine", "Utilization Percentage", name, true)); }
                    catch { /* skip */ }
                }
                HasGpu = _gpuEngines.Count > 0;
                if (!HasGpu) _gpuEngines = null;
            }
            catch { _gpuEngines = null; HasGpu = false; }
            _gpuRefreshCountdown = 10; // engine list churns with processes; rebuild periodically
        }

        private static double SafeRead(PerformanceCounter c)
        {
            try
            {
                float v = c.NextValue();
                return double.IsNaN(v) || double.IsInfinity(v) ? double.NaN : v;
            }
            catch { return double.NaN; }
        }

        public async Task SampleAsync()
        {
            lock (_gate)
            {
                if (_sampling || _disposed) return;
                _sampling = true;
            }
            try
            {
                Task<double> pingTask;
                lock (_gate)
                {
                    if (--_pingCountdown <= 0)
                    {
                        _pingCountdown = PingEverySamples;
                        pingTask = PingOnceAsync();
                    }
                    else
                    {
                        pingTask = Task.FromResult(double.NaN);
                    }
                }
                double cpu = double.NaN, gpu = double.NaN, ram = double.NaN,
                       disk = double.NaN, netMbps = double.NaN;
                await Task.Run(() =>
                {
                    EnsureCounters();
                    if (_cpu != null)
                    {
                        double v = SafeRead(_cpu);
                        if (!double.IsNaN(v)) cpu = Math.Max(0, Math.Min(100, v));
                    }
                    if (_ram != null)
                    {
                        double v = SafeRead(_ram);
                        if (!double.IsNaN(v)) ram = Math.Max(0, Math.Min(100, v));
                    }
                    if (_disk != null)
                    {
                        double v = SafeRead(_disk);
                        if (!double.IsNaN(v)) disk = Math.Max(0, Math.Min(100, v));
                    }
                    if (_net != null)
                    {
                        double total = 0;
                        bool any = false;
                        foreach (var c in _net)
                        {
                            double v = SafeRead(c);
                            if (!double.IsNaN(v)) { total += v; any = true; }
                        }
                        if (any) netMbps = Math.Max(0, total * 8.0 / 1000000.0);
                    }
                    if (_gpuEngines != null)
                    {
                        if (--_gpuRefreshCountdown <= 0) RefreshGpuEngines();
                        if (_gpuEngines != null)
                        {
                            double total = 0;
                            bool any = false;
                            foreach (var c in _gpuEngines)
                            {
                                double v = SafeRead(c);
                                if (!double.IsNaN(v)) { total += v; any = true; }
                            }
                            if (any) gpu = Math.Max(0, Math.Min(100, total));
                        }
                    }
                }).ConfigureAwait(false);
                double ping = await pingTask.ConfigureAwait(false);

                lock (_gate)
                {
                    if (_disposed) return;
                    Cpu = cpu; Gpu = gpu; Ram = ram; Disk = disk; NetMbps = netMbps;
                    _cpuHist.Push(cpu); _gpuHist.Push(gpu); _ramHist.Push(ram);
                    _diskHist.Push(disk); _netHist.Push(netMbps);
                    // PingMs keeps its last value between pings so the readout
                    // stays stable instead of flickering every second.
                    if (!double.IsNaN(ping)) { PingMs = ping; _pingHist.Push(ping); }
                }
            }
            finally
            {
                lock (_gate) { _sampling = false; }
            }
        }

        private async Task<double> PingOnceAsync()
        {
            try
            {
                var reply = await _ping.SendPingAsync("1.1.1.1", 900).ConfigureAwait(false);
                if (reply != null && reply.Status == IPStatus.Success)
                    return (double)reply.RoundtripTime;
            }
            catch { /* no network or blocked ICMP */ }
            return double.NaN;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { _cpu?.Dispose(); } catch { }
            try { _ram?.Dispose(); } catch { }
            try { _disk?.Dispose(); } catch { }
            if (_net != null) foreach (var c in _net) { try { c.Dispose(); } catch { } }
            if (_gpuEngines != null) foreach (var c in _gpuEngines) { try { c.Dispose(); } catch { } }
            try { _ping.Dispose(); } catch { }
        }

        private sealed class VitalHistory
        {
            private readonly Queue<double> _q;
            private readonly int _capacity;
            public VitalHistory(int capacity) { _capacity = capacity; _q = new Queue<double>(capacity); }
            public void Push(double v)
            {
                lock (_q)
                {
                    while (_q.Count >= _capacity) _q.Dequeue();
                    _q.Enqueue(v);
                }
            }
            public double[] Snapshot()
            {
                lock (_q) return _q.ToArray();
            }
        }
    }
}
