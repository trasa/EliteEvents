using System.Text;
using EliteEvents.Eddn.Config;
using Ionic.Zlib;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;

namespace EliteEvents.Eddn;

public interface IEddnStream : IDisposable
{
    void Connect();

    /// <summary>
    /// Tears down the current socket and establishes a fresh connection. Used to recover
    /// when the upstream EDDN relay has silently dropped or stalled the subscription.
    /// </summary>
    void Reconnect();

    /// <summary>
    /// Returns the next decompressed message, or null if no frame arrived within
    /// <see cref="EddnOptions.ReceiveTimeout"/> (a timeout, not an error).
    /// </summary>
    string? Receive();
}

public class EddnStream : IEddnStream
{
    private readonly EddnOptions _options;
    private readonly UTF8Encoding _utf8Encoding = new();
    private SubscriberSocket _client;

    public EddnStream(IOptions<EddnOptions> options)
    {
        _options = options.Value;
        _client = CreateSocket();
    }

    private SubscriberSocket CreateSocket()
    {
        var socket = new SubscriberSocket();
        socket.Options.ReceiveHighWatermark = _options.ReceiveHighWatermark;
        return socket;
    }

    public void Connect()
    {
        _client.Connect(_options.StreamUrl);
        _client.SubscribeToAnyTopic();
    }

    public void Reconnect()
    {
        _client.Dispose();
        _client = CreateSocket();
        Connect();
    }

    public string? Receive()
    {
        if (!_client.TryReceiveFrameBytes(_options.ReceiveTimeout, out var bytes)
            || bytes is not { Length: > 0 })
        {
            return null;
        }
        var uncompressed = ZlibStream.UncompressBuffer(bytes);
        return uncompressed != null ? _utf8Encoding.GetString(uncompressed) : null;
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
