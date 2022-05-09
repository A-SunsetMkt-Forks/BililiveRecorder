using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using BililiveRecorder.Core.Api;
using BililiveRecorder.Core.Config;
using BililiveRecorder.Core.Event;
using BililiveRecorder.Core.Templating;
using Serilog;
using Timer = System.Timers.Timer;

namespace BililiveRecorder.Core.Recording
{
    public abstract class RecordTaskBase : IRecordTask
    {
        private const string HttpHeaderAccept = "*/*";
        private const string HttpHeaderOrigin = "https://live.bilibili.com";
        private const string HttpHeaderReferer = "https://live.bilibili.com/";
        private const string HttpHeaderUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/101.0.4951.54 Safari/537.36";

        private const int timer_inverval = 2;
        protected readonly Timer timer = new Timer(1000 * timer_inverval);
        protected readonly Random random = new Random();
        protected readonly CancellationTokenSource cts = new CancellationTokenSource();
        protected readonly CancellationToken ct;

        protected readonly IRoom room;
        protected readonly ILogger logger;
        protected readonly IApiClient apiClient;
        private readonly FileNameGenerator fileNameGenerator;

        protected string? streamHost;
        protected bool started = false;
        protected bool timeoutTriggered = false;


        private readonly object ioStatsLock = new();
        protected int ioNetworkDownloadedBytes;

        protected Stopwatch ioDiskStopwatch = new();
        protected object ioDiskStatsLock = new();
        protected TimeSpan ioDiskWriteDuration;
        protected int ioDiskWrittenBytes;
        private DateTimeOffset ioDiskWarningTimeout;

        private DateTimeOffset ioStatsLastTrigger;
        private TimeSpan durationSinceNoDataReceived;

        protected RecordTaskBase(IRoom room, ILogger logger, IApiClient apiClient, FileNameGenerator fileNameGenerator)
        {
            this.room = room ?? throw new ArgumentNullException(nameof(room));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            this.apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            this.fileNameGenerator = fileNameGenerator ?? throw new ArgumentNullException(nameof(fileNameGenerator));
            this.ct = this.cts.Token;

            this.timer.Elapsed += this.Timer_Elapsed_TriggerIOStats;
        }

        public Guid SessionId { get; } = Guid.NewGuid();

        #region Events

        public event EventHandler<IOStatsEventArgs>? IOStats;
        public event EventHandler<RecordingStatsEventArgs>? RecordingStats;
        public event EventHandler<RecordFileOpeningEventArgs>? RecordFileOpening;
        public event EventHandler<RecordFileClosedEventArgs>? RecordFileClosed;
        public event EventHandler? RecordSessionEnded;

        protected void OnIOStats(IOStatsEventArgs e) => IOStats?.Invoke(this, e);
        protected void OnRecordingStats(RecordingStatsEventArgs e) => RecordingStats?.Invoke(this, e);
        protected void OnRecordFileOpening(RecordFileOpeningEventArgs e) => RecordFileOpening?.Invoke(this, e);
        protected void OnRecordFileClosed(RecordFileClosedEventArgs e) => RecordFileClosed?.Invoke(this, e);
        protected void OnRecordSessionEnded(EventArgs e) => RecordSessionEnded?.Invoke(this, e);

        #endregion

        public virtual void RequestStop() => this.cts.Cancel();

        public virtual void SplitOutput() { }

        public async virtual Task StartAsync()
        {
            if (this.started)
                throw new InvalidOperationException("Only one StartAsync call allowed per instance.");
            this.started = true;

            var (fullUrl, qn) = await this.FetchStreamUrlAsync(this.room.RoomConfig.RoomId).ConfigureAwait(false);

            var uri = new Uri(fullUrl);
            this.streamHost = uri.Host;
            var qnDesc = qn switch
            {
                20000 => "4K",
                10000 => "原画",
                401 => "蓝光(杜比)",
                400 => "蓝光",
                250 => "超清",
                150 => "高清",
                80 => "流畅",
                _ => "未知"
            };
            this.logger.Information("连接直播服务器 {Host} 录制画质 {Qn} ({QnDescription})", this.streamHost, qn, qnDesc);
            this.logger.Debug("直播流地址 {Url}", fullUrl);

            if (Regex.IsMatch(uri.Host, @"^cn-.+\.bilivideo\.com$"))
            {
                var b = new UriBuilder(fullUrl)
                {
                    Scheme = "https",
                    Host = @"d1--cn-gotcha01.bilivideo.com",
                    Port = 443
                };
                fullUrl = b.ToString();
                this.logger.Information("魔改直播流地址到 {Url}", fullUrl);
            }

            var stream = await this.GetStreamAsync(fullUrl: fullUrl, timeout: (int)this.room.RoomConfig.TimingStreamConnect).ConfigureAwait(false);

            this.ioStatsLastTrigger = DateTimeOffset.UtcNow;
            this.durationSinceNoDataReceived = TimeSpan.Zero;

            this.ct.Register(state => Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000);
                    if (((WeakReference<Stream>)state).TryGetTarget(out var weakStream))
                        weakStream.Dispose();
                }
                catch (Exception)
                { }
            }), state: new WeakReference<Stream>(stream), useSynchronizationContext: false);

            this.StartRecordingLoop(stream);
        }

        protected abstract void StartRecordingLoop(Stream stream);

        private void Timer_Elapsed_TriggerIOStats(object sender, ElapsedEventArgs e)
        {
            int networkDownloadBytes, diskWriteBytes;
            TimeSpan durationDiff, diskWriteDuration;
            DateTimeOffset startTime, endTime;


            lock (this.ioStatsLock) // 锁 timer elapsed 事件本身防止并行运行
            {
                // networks
                networkDownloadBytes = Interlocked.Exchange(ref this.ioNetworkDownloadedBytes, 0); // 锁网络统计
                endTime = DateTimeOffset.UtcNow;
                startTime = this.ioStatsLastTrigger;
                this.ioStatsLastTrigger = endTime;
                durationDiff = endTime - startTime;

                this.durationSinceNoDataReceived = networkDownloadBytes > 0 ? TimeSpan.Zero : this.durationSinceNoDataReceived + durationDiff;

                // disks
                lock (this.ioDiskStatsLock) // 锁硬盘统计
                {
                    diskWriteDuration = this.ioDiskWriteDuration;
                    diskWriteBytes = this.ioDiskWrittenBytes;
                    this.ioDiskWriteDuration = TimeSpan.Zero;
                    this.ioDiskWrittenBytes = 0;
                }
            }

            var netMbps = networkDownloadBytes * (8d / 1024d / 1024d) / durationDiff.TotalSeconds;
            var diskMBps = diskWriteBytes / (1024d * 1024d) / diskWriteDuration.TotalSeconds;

            this.OnIOStats(new IOStatsEventArgs
            {
                NetworkBytesDownloaded = networkDownloadBytes,
                Duration = durationDiff,
                StartTime = startTime,
                EndTime = endTime,
                NetworkMbps = netMbps,
                DiskBytesWritten = diskWriteBytes,
                DiskWriteDuration = diskWriteDuration,
                DiskMBps = diskMBps,
            });

            var now = DateTimeOffset.Now;
            if (diskWriteBytes > 0 && this.ioDiskWarningTimeout < now && (diskWriteDuration.TotalSeconds > 1d || diskMBps < 2d))
            {
                // 硬盘 IO 可能不能满足录播
                this.ioDiskWarningTimeout = now + TimeSpan.FromMinutes(2); // 最多每 2 分钟提醒一次
                this.logger.Warning("检测到硬盘写入速度较慢可能影响录播，请检查是否有其他软件或游戏正在使用硬盘");
            }

            if ((!this.timeoutTriggered) && (this.durationSinceNoDataReceived.TotalMilliseconds > this.room.RoomConfig.TimingWatchdogTimeout))
            {
                this.timeoutTriggered = true;
                this.logger.Warning("检测到录制卡住，可能是网络或硬盘原因，将会主动断开连接");
                this.RequestStop();
            }
        }

        protected (string fullPath, string relativePath) CreateFileName() => this.fileNameGenerator.CreateFilePath(new FileNameGenerator.FileNameContextData
        {
            Name = FileNameGenerator.RemoveInvalidFileName(this.room.Name, ignore_slash: false),
            Title = FileNameGenerator.RemoveInvalidFileName(this.room.Title, ignore_slash: false),
            RoomId = this.room.RoomConfig.RoomId,
            ShortId = this.room.ShortId,
            AreaParent = FileNameGenerator.RemoveInvalidFileName(this.room.AreaNameParent, ignore_slash: false),
            AreaChild = FileNameGenerator.RemoveInvalidFileName(this.room.AreaNameChild, ignore_slash: false),
            Json = this.room.RawBilibiliApiJsonData,
        });

        #region Api Requests

        private HttpClient CreateHttpClient()
        {
            var httpClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseProxy = this.room.RoomConfig.NetworkTransportUseSystemProxy,
            });
            var headers = httpClient.DefaultRequestHeaders;
            headers.Add("Accept", HttpHeaderAccept);
            headers.Add("Origin", HttpHeaderOrigin);
            headers.Add("Referer", HttpHeaderReferer);
            headers.Add("User-Agent", HttpHeaderUserAgent);
            return httpClient;
        }

        protected async Task<(string url, int qn)> FetchStreamUrlAsync(int roomid)
        {
            const int DefaultQn = 10000;
            var selected_qn = DefaultQn;
            var codecItem = await this.apiClient.GetCodecItemInStreamUrlAsync(roomid: roomid, qn: DefaultQn).ConfigureAwait(false);

            if (codecItem is null)
                throw new Exception("no supported stream url, qn: " + DefaultQn);

            var qns = this.room.RoomConfig.RecordingQuality?.Split(new[] { ',', '，', '、', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out var num) ? num : -1)
                .Where(x => x > 0)
                .ToArray()
                ?? Array.Empty<int>();

            // Select first avaiable qn
            foreach (var qn in qns)
            {
                if (codecItem.AcceptQn.Contains(qn))
                {
                    selected_qn = qn;
                    goto match_qn_success;
                }
            }

            this.logger.Information("没有符合设置要求的画质，稍后再试。设置画质 {QnSettings}, 可用画质 {AcceptQn}", qns, codecItem.AcceptQn);
            throw new NoMatchingQnValueException();

        match_qn_success:
            this.logger.Debug("设置画质 {QnSettings}, 可用画质 {AcceptQn}, 最终选择 {SelectedQn}", qns, codecItem.AcceptQn, selected_qn);

            if (selected_qn != DefaultQn)
            {
                // 最终选择的 qn 与默认不同，需要重新请求一次
                codecItem = await this.apiClient.GetCodecItemInStreamUrlAsync(roomid: roomid, qn: selected_qn).ConfigureAwait(false);

                if (codecItem is null)
                    throw new Exception("no supported stream url, qn: " + selected_qn);
            }

            if (codecItem.CurrentQn != selected_qn || !qns.Contains(codecItem.CurrentQn))
                this.logger.Warning("返回的直播流地址的画质是 {CurrentQn} 而不是请求的 {SelectedQn}", codecItem.CurrentQn, selected_qn);

            var url_infos = codecItem.UrlInfos;
            if (url_infos is null || url_infos.Length == 0)
                throw new Exception("no url_info");

            // https:// xy0x0x0x0xy.mcdn.bilivideo.cn:486
            var url_infos_without_mcdn = url_infos.Where(x => !x.Host.Contains(".mcdn.")).ToArray();

            var url_info = url_infos_without_mcdn.Length != 0
                ? url_infos_without_mcdn[this.random.Next(url_infos_without_mcdn.Length)]
                : url_infos[this.random.Next(url_infos.Length)];

            var fullUrl = url_info.Host + codecItem.BaseUrl + url_info.Extra;
            return (fullUrl, codecItem.CurrentQn);
        }

        protected async Task<Stream> GetStreamAsync(string fullUrl, int timeout)
        {
            var client = this.CreateHttpClient();

            while (true)
            {
                var originalUri = new Uri(fullUrl);
                var allowedAddressFamily = this.room.RoomConfig.NetworkTransportAllowedAddressFamily;
                HttpRequestMessage request;

                if (allowedAddressFamily == AllowedAddressFamily.System)
                {
                    this.logger.Debug("NetworkTransportAllowedAddressFamily is System");
                    request = new HttpRequestMessage(HttpMethod.Get, originalUri);
                }
                else
                {
                    var ips = await Dns.GetHostAddressesAsync(originalUri.DnsSafeHost);

                    var filtered = ips.Where(x => allowedAddressFamily switch
                    {
                        AllowedAddressFamily.Ipv4 => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork,
                        AllowedAddressFamily.Ipv6 => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6,
                        AllowedAddressFamily.Any => true,
                        _ => false
                    }).ToArray();

                    var selected = filtered[this.random.Next(filtered.Length)];

                    this.logger.Debug("指定直播服务器地址 {DnsHost}: {SelectedIp}, Allowed: {AllowedAddressFamily}, {IPAddresses}", originalUri.DnsSafeHost, selected, allowedAddressFamily, ips);

                    if (selected is null)
                    {
                        throw new Exception("DNS 没有返回符合要求的 IP 地址");
                    }

                    var builder = new UriBuilder(originalUri)
                    {
                        Host = selected.ToString()
                    };

                    request = new HttpRequestMessage(HttpMethod.Get, builder.Uri);
                    request.Headers.Host = originalUri.IsDefaultPort ? originalUri.Host : originalUri.Host + ":" + originalUri.Port;
                }

                var resp = await client.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead,
                    new CancellationTokenSource(timeout).Token)
                    .ConfigureAwait(false);

                switch (resp.StatusCode)
                {
                    case HttpStatusCode.OK:
                        {
                            this.logger.Information("开始接收直播流");
                            var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                            return stream;
                        }
                    case HttpStatusCode.Moved:
                    case HttpStatusCode.Redirect:
                        {
                            fullUrl = resp.Headers.Location.OriginalString;
                            this.logger.Debug("跳转到 {Url}", fullUrl);
                            resp.Dispose();
                            break;
                        }
                    default:
                        throw new Exception(string.Format("尝试下载直播流时服务器返回了 ({0}){1}", resp.StatusCode, resp.ReasonPhrase));
                }
            }
        }
        #endregion
    }
}
