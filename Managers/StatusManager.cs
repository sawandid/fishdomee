using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using System.Text;
using LibreHardwareMonitor.Hardware;
using System.Reflection;

namespace dcrpt_miner
{
    public class StatusManager : IHostedService
    {
        public static long Shares;
        public static long AcceptedShares;
        public static long RejectedShares;
        public static long DroppedShares;
        public static ulong[] CpuHashCount = new ulong[0];
        public static ulong[] GpuHashCount = new ulong[0];
        public static string AlgoName = "n/a";
        public static IConnectionProvider ConnectionProvider;

        private static Stopwatch Watch { get; set; }
        private static SpinLock SpinLock = new SpinLock();
        private static List<Snapshot> HashrateSnapshots = new List<Snapshot>();
        private IConfiguration Configuration { get; }
        private CancellationTokenSource ThreadSource = new CancellationTokenSource();

        public StatusManager(IConfiguration configuration)
        {
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Watch = new Stopwatch();
            Watch.Start();

            new Thread(() => PeriodicReportTimer(ThreadSource.Token))
                .UnsafeStart();
            new Thread(() => CollectHashrate(ThreadSource.Token))
                .UnsafeStart();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            ThreadSource.Cancel();
            return Task.CompletedTask;
        }

        public static Stats QueryStats()
        {
            ulong hashes = 0;

            foreach (var h in CpuHashCount) {
                hashes += h;
            }

            foreach (var h in GpuHashCount) {
                hashes += h;
            }

            return new Stats
            {
                hashes = hashes,
                uptime = Convert.ToInt64(Watch.Elapsed.TotalSeconds),
                ver = Assembly.GetEntryAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion.ToString(),
                accepted = AcceptedShares,
                rejected = RejectedShares
            };
        }

        public static ulong GetHashrate(String type, int id, TimeSpan from)
        {
            bool lockTaken = false;
            try {
                SpinLock.Enter(ref lockTaken);

                var timestampFrom = DateTime.Now - from;

                var snapshot = HashrateSnapshots.Where(p => p.type == type && p.id == id && p.timestamp >= timestampFrom)
                    .MinBy(p => p.timestamp);

                var latest = HashrateSnapshots.Where(p => p.type == type && p.id == id)
                    .MaxBy(p => p.timestamp);

                if (snapshot == null || latest == null) {
                    return 0;
                }
                
                if (snapshot.hashrate == latest.hashrate) {
                    // hashrate has not changed, find previous record to compare against
                    snapshot = HashrateSnapshots.Where(p => p.type == type && p.id == id && p.hashrate < latest.hashrate)
                        .MaxBy(p => p.timestamp);
                }

                if (snapshot == null) {
                    snapshot = new Snapshot
                    {
                        timestamp = DateTime.Now.AddMilliseconds(Watch.ElapsedMilliseconds * -1),
                        hashrate = 0
                    };
                }

                var timeBetween = latest.timestamp - snapshot.timestamp;
                var hashesBetween = latest.hashrate - snapshot.hashrate;

                return (ulong)(hashesBetween / timeBetween.TotalSeconds);
            }
            catch (Exception ex) {
                SafeConsole.WriteLine(ConsoleColor.DarkRed, ex.ToString());
                return 0;
            }
            finally {
                if (lockTaken) {
                    SpinLock.Exit(false);
                }
            }
        }

        public static void DoPeriodicReport() 
        {
            CollectHashrateSnapshot();

            var accepted = Interlocked.Read(ref AcceptedShares);
            var dropped = Interlocked.Read(ref DroppedShares);
            var rejected = Interlocked.Read(ref RejectedShares);
            var total = (double)(accepted + dropped + rejected);

            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine("OK");

            ulong totalHashes = 0;
            

            if (GpuHashCount.Length > 0) {
                for (int i = 0; i < GpuHashCount.Length; i++) {
                var hashes = GetHashrate("GPU", i, TimeSpan.FromMinutes(1));
                CalculateUnit(hashes, out double gpu_1m_hashrate, out string gpu_1m_unit);
                CalculateUnit(GetHashrate("GPU", i, TimeSpan.FromMinutes(5)), out double gpu_5m_hashrate, out string gpu_5m_unit);
                CalculateUnit(GetHashrate("GPU", i, TimeSpan.FromMinutes(30)), out double gpu_30m_hashrate, out string gpu_30m_unit);
                    sb.AppendFormat("| Halan (GPU #{0}) \t{1:N2} {2}\t{3:N2} {4}\t{5:N2} {6}{7}",
                        i,
                        gpu_1m_hashrate, gpu_1m_unit,
                        gpu_5m_hashrate, gpu_5m_unit,
                        gpu_30m_hashrate, gpu_30m_unit,
                        Environment.NewLine);

                    totalHashes += hashes;
                }
            }




            
            SafeConsole.WriteLine(ConsoleColor.White, sb.ToString());
        }

        public static void PrintHelp()
        {
            var helpMsg = @"|Nyosot";
            SafeConsole.WriteLine(ConsoleColor.White, helpMsg);
        }

        public static void RegisterAlgorith(IAlgorithm algo)
        {
            AlgoName = algo.Name;
            HashrateSnapshots.Clear();
        }

        public static void RegisterConnectionProvider(IConnectionProvider connectionProvider)
        {
            ConnectionProvider = connectionProvider;
        }

        private static void CollectHashrateSnapshot()
        {
            bool lockTaken = false;
            try {
                SpinLock.Enter(ref lockTaken);
                ulong hashes = 0;

                // keep only snapshots from past 30 minutes
                var expiredAt = DateTime.Now.AddMinutes(-30);
                HashrateSnapshots.RemoveAll(p => p.timestamp <= expiredAt);

                if (CpuHashCount.Length > 0) {
                    for (int i = 0; i < CpuHashCount.Length; i++) {
                        hashes += CpuHashCount[i];
                    }

                    HashrateSnapshots.Add(new Snapshot {
                        timestamp = DateTime.Now,
                        type = "CPU",
                        id = 0,
                        hashrate = hashes
                    });
                }

                if (GpuHashCount.Length > 0) {
                    for (int i = 0; i < GpuHashCount.Length; i++) {
                        HashrateSnapshots.Add(new Snapshot {
                            timestamp = DateTime.Now,
                            type = "GPU",
                            id = i,
                            hashrate = GpuHashCount[i]
                        });

                        hashes += GpuHashCount[i];
                    }
                }

                HashrateSnapshots.Add(new Snapshot {
                    timestamp = DateTime.Now,
                    type = "TOTAL",
                    id = 0,
                    hashrate = hashes
                });
            } catch (Exception ex) {
                SafeConsole.WriteLine(ConsoleColor.DarkRed, ex.ToString());
            }
            finally {
                if (lockTaken) {
                    SpinLock.Exit(false);
                }
            }
        }

        private void CollectHashrate(CancellationToken token)
        {
            var gpuEnabled = Configuration.GetValue<bool>("gpu:enabled");
            var cpuEnabled = Configuration.GetValue<bool>("cpu:enabled");

            while(!token.IsCancellationRequested) {
                CollectHashrateSnapshot();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
            }
        }

        private void PeriodicReportTimer(CancellationToken token)
        {
            var delay = Configuration.GetValue<int>("periodic_report:initial_delay", 30);
            var interval = Configuration.GetValue<int>("periodic_report:report_interval", 180);

            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(delay));

            while(!token.IsCancellationRequested) {
                DoPeriodicReport();
                token.WaitHandle.WaitOne(TimeSpan.FromSeconds(interval));
            }
        }

        public static void CalculateUnit(double hashrate, out double adjusted_hashrate, out string unit) {
            if (hashrate > 1000000000000) {
                adjusted_hashrate = hashrate / 1000000000000;
                unit = "TH/s";
                return;
            }

            if (hashrate > 1000000000) {
                adjusted_hashrate = hashrate / 1000000000;
                unit = "GH/s";
                return;
            }

            if (hashrate > 1000000) {
                adjusted_hashrate = hashrate / 1000000;
                unit = "MH/s";
                return;
            }

            if (hashrate > 1000) {
                adjusted_hashrate = hashrate / 1000;
                unit = "KH/s";
                return;
            }

            adjusted_hashrate = hashrate;
            unit = "H/s";
        }

        public class Snapshot {
            public DateTime timestamp { get; set; }
            public String type { get; set; }
            public int id { get; set; }
            public ulong hashrate { get; set; }
        }

        public class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer)
            {
                computer.Traverse(this);
            }
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (IHardware subHardware in hardware.SubHardware) subHardware.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }
    }
}
