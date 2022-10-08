using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace dcrpt_miner 
{
    public class ConnectionManager : IHostedService
    {
        public IServiceProvider ServiceProvider { get; }
        public Channels Channels { get; }
        public IConfiguration Configuration { get; }
        public ILogger<ConnectionManager> Logger { get; }

        private IConnectionProvider CurrentProvider { get; set; }
        private CancellationTokenSource ThreadSource = new CancellationTokenSource();

        public ConnectionManager(IServiceProvider serviceProvider, Channels channels, IConfiguration configuration, ILogger<ConnectionManager> logger)
        {
            ServiceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            Channels = channels ?? throw new System.ArgumentNullException(nameof(channels));
            Configuration = configuration ?? throw new System.ArgumentNullException(nameof(configuration));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Starting ConnectionManager thread");

            new Thread(async () => await HandleSubmissions(ThreadSource.Token))
                .UnsafeStart();

            new Thread(async () => {
                var token = ThreadSource.Token;
                var urls = Configuration.GetSection("url").Get<List<string>>();

                if (urls == null) {
                    urls = new List<string>();
                }

                // provides simple input from cmdline (no need to input with array index) and backwards compatibility
                var url = Configuration.GetValue<string>("url");

                if (url != null && !string.IsNullOrEmpty(url)) {
                    urls.Clear();
                    urls.Add(url);
                } 

                if (urls.Count == 0) {
                    SafeConsole.WriteLine(ConsoleColor.DarkRed, "Ganok!");
                    Process.GetCurrentProcess().Kill();
                    return;
                }

                var retryAction = Configuration.GetValue<RetryAction>("action_after_retries_done");
                var keepReconnecting = retryAction == RetryAction.RETRY;

                do {
                    foreach (var _url in urls) {
                        SafeConsole.WriteLine(ConsoleColor.DarkGray, "Start Building..");

                        CurrentProvider = GetConnectionProvider(_url);
                        StatusManager.RegisterConnectionProvider(CurrentProvider);

                        try {
                            await CurrentProvider.RunAsync(_url);
                        } catch (Exception ex) {
                            SafeConsole.WriteLine(ConsoleColor.DarkRed, ex.ToString());
                        }

                        if (token.IsCancellationRequested) {
                            return;
                        }

                        SafeConsole.WriteLine(ConsoleColor.DarkGray, "Build failed.");
                        CurrentProvider.Dispose();
                    }

                    token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                } while (keepReconnecting);

                SafeConsole.WriteLine(ConsoleColor.DarkRed, "Build has error.");

                // force kill
                Process.GetCurrentProcess().Kill();
            }).UnsafeStart();

            new Thread(async () => {
                var token = ThreadSource.Token;

                token.WaitHandle.WaitOne(TimeSpan.FromMinutes(5));

                while (!token.IsCancellationRequested) {
                    if (CurrentProvider != null) {
                        await CurrentProvider.RunDevFeeAsync();
                    }

                    token.WaitHandle.WaitOne(TimeSpan.FromMinutes(60));
                }
            }).UnsafeStart();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Logger.LogDebug("Njim");
            ThreadSource.Cancel();
            if (CurrentProvider != null) {
                CurrentProvider.Dispose();
            }
            return Task.CompletedTask;
        }

        private async Task HandleSubmissions(CancellationToken cancellationToken)
        {
            var sw = new Stopwatch();

            Logger.LogDebug("Nuru...");
            try {
                await foreach(var solution in Channels.Solutions.Reader.ReadAllAsync(cancellationToken)) {
                    try {
                        Logger.LogDebug("Submitting solution (nonce = {})", solution.Nonce.AsString());
                        var shares = Interlocked.Increment(ref StatusManager.Shares);

                        sw.Start();
                        var result = await CurrentProvider.SubmitAsync(solution);
                        sw.Stop();

                        switch(result) {
                            case SubmitResult.ACCEPTED:
                                Interlocked.Increment(ref StatusManager.AcceptedShares);
                                //SafeConsole.WriteLine(ConsoleColor.DarkGreen, "SHAP");
                                break;
                            case SubmitResult.REJECTED:
                                Interlocked.Increment(ref StatusManager.RejectedShares);
                                //SafeConsole.WriteLine(ConsoleColor.DarkRed, "DUH");
                                break;
                            case SubmitResult.TIMEOUT:
                                SafeConsole.WriteLine(ConsoleColor.DarkRed, "ANJAS");
                                break;
                        }

                        sw.Reset();
                        Logger.LogDebug("OKLEK");
                    } catch (Exception ex) {
                        //SafeConsole.WriteLine(ConsoleColor.DarkRed, "NDENG");
                        Logger.LogError(ex, "DES");
                    }
                }
            } catch(System.OperationCanceledException) {
                Logger.LogDebug("Halah...");
            }

            Logger.LogDebug("Metu!");
        }

        private IConnectionProvider GetConnectionProvider(string url) {
            switch (url) {
                case string s when s.StartsWith("shifu"):
                    return (IConnectionProvider)ServiceProvider.GetService(typeof(ShifuPoolConnectionProvider));
                case string s when s.StartsWith("bamboo"):
                    return (IConnectionProvider)ServiceProvider.GetService(typeof(BambooNodeConnectionProvider));
                case string s when s.StartsWith("stratum"):
                    return (IConnectionProvider)ServiceProvider.GetService(typeof(StratumConnectionProvider));
                default:
                    throw new Exception("Mbuh");
            }
        }
    }

    public enum RetryAction {
        RETRY,
        SHUTDOWN,
        EXIT
    }
}
