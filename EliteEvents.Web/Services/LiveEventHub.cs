using System.Collections.Concurrent;
using System.Threading.Channels;
using EliteEvents.Eddn.Storage;
using Newtonsoft.Json;

namespace EliteEvents.Web.Services;

/// <summary>One ticker event, in both of the forms the SSE endpoint sends.</summary>
/// <param name="Json">The wire format the Next dashboard's ticker consumed, kept as-is.</param>
/// <param name="Html">The rendered <c>&lt;li&gt;</c> htmx swaps into the page.</param>
public sealed record TickerFrame(string Json, string Html);

/// <summary>
/// Per-pod fan-out for the live ticker: <em>one</em> Redis subscription feeds every SSE client
/// connected to this pod, each through its own bounded channel.
/// <para>
/// The Next dashboard opened a fresh Redis subscriber connection per browser — its README flagged
/// that as a TODO. Fan-out also means replicas need no coordination: every pod holds its own
/// subscription and every client sees every event, whichever pod it landed on.
/// </para>
/// </summary>
public sealed class LiveEventHub : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// How many recent events a freshly loaded page is seeded with, so the ticker isn't an empty
    /// box for the first few seconds. Per-pod and purely a cache — two pods may seed slightly
    /// different rows, which is fine; nothing here is session state.
    /// </summary>
    public const int RecentEventCapacity = 40;

    /// <summary>
    /// A client that can't keep up drops its oldest pending rows rather than stalling the hub.
    /// </summary>
    private const int ClientQueueCapacity = 100;

    private static readonly TimeSpan SubscribeRetryDelay = TimeSpan.FromSeconds(30);

    private readonly IEventSubscriber _subscriber;
    private readonly TickerFragmentRenderer _renderer;
    private readonly ILogger<LiveEventHub> _logger;

    private readonly ConcurrentDictionary<Guid, Channel<TickerFrame>> _clients = new();
    private readonly Queue<TickerFrame> _recent = new(RecentEventCapacity);
    private readonly Lock _recentLock = new();

    private CancellationTokenSource? _stopping;
    private Task? _connecting;
    private IAsyncDisposable? _subscription;

    public LiveEventHub(IEventSubscriber subscriber, TickerFragmentRenderer renderer, ILogger<LiveEventHub> logger)
    {
        _subscriber = subscriber;
        _renderer = renderer;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = new CancellationTokenSource();
        // Connecting in the background rather than awaiting here: a Redis that is slow or absent
        // at startup must not hold up the web tier, which can still serve every page from data
        // that is already there.
        _connecting = ConnectAsync(_stopping.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null)
        {
            await _stopping.CancelAsync();
        }

        if (_connecting is not null)
        {
            await _connecting;
        }

        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
            _subscription = null;
        }

        foreach (var client in _clients.Values)
        {
            client.Writer.TryComplete();
        }
    }

    /// <summary>
    /// Attaches a new SSE client. Dispose the returned subscription when the response ends.
    /// </summary>
    public ClientSubscription SubscribeClient()
    {
        var channel = Channel.CreateBounded<TickerFrame>(new BoundedChannelOptions(ClientQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();
        _clients[id] = channel;
        return new ClientSubscription(this, id, channel.Reader);
    }

    /// <summary>Most recent frames, newest first.</summary>
    public IReadOnlyList<TickerFrame> RecentFrames()
    {
        lock (_recentLock)
        {
            return _recent.Reverse().ToList();
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                _subscription = await _subscriber.SubscribeAsync(OnEventAsync);
                _logger.LogInformation("Subscribed to the live event channel");
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not subscribe to the live event channel; retrying in {Delay}",
                    SubscribeRetryDelay);
            }

            try
            {
                await Task.Delay(SubscribeRetryDelay, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task OnEventAsync(LiveEvent liveEvent)
    {
        string html;
        try
        {
            html = await _renderer.RenderAsync(liveEvent);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to render a {EventType} ticker row", liveEvent.Type);
            return;
        }

        var frame = new TickerFrame(JsonConvert.SerializeObject(liveEvent), html);

        lock (_recentLock)
        {
            _recent.Enqueue(frame);
            while (_recent.Count > RecentEventCapacity)
            {
                _recent.Dequeue();
            }
        }

        foreach (var client in _clients.Values)
        {
            client.Writer.TryWrite(frame);
        }
    }

    private void Unsubscribe(Guid id) => _clients.TryRemove(id, out _);

    public async ValueTask DisposeAsync()
    {
        _stopping?.Dispose();
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    public sealed class ClientSubscription : IDisposable
    {
        private readonly LiveEventHub _hub;
        private readonly Guid _id;

        internal ClientSubscription(LiveEventHub hub, Guid id, ChannelReader<TickerFrame> reader)
        {
            _hub = hub;
            _id = id;
            Reader = reader;
        }

        public ChannelReader<TickerFrame> Reader { get; }

        public void Dispose() => _hub.Unsubscribe(_id);
    }
}
