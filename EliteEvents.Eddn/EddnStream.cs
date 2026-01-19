using System.Text;
using EliteEvents.Eddn.Config;
using Ionic.Zlib;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetMQ;
using NetMQ.Sockets;

namespace EliteEvents.Eddn;

public interface IEddnStream : IDisposable
{
    void Connect();

    string? Receive();
}

public class EddnStream : IEddnStream
{
    private readonly ILogger<EddnStream> _logger;
    private readonly EddnOptions _options;
    private readonly UTF8Encoding _utf8Encoding = new UTF8Encoding();
    private readonly SubscriberSocket  _client;

    public EddnStream(ILogger<EddnStream> logger,
        IOptions<EddnOptions> options)
    {
        _logger = logger;
        _options = options.Value;
        _client = new SubscriberSocket();
        _client.Options.ReceiveHighWatermark = _options.ReceiveHighWatermark;
    }

    public void Connect()
    {
        _client.Connect(_options.StreamUrl);
        _client.SubscribeToAnyTopic();
    }

    public string? Receive()
    {
        var bytes = _client.ReceiveFrameBytes();
        if (bytes.Length == 0)
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
