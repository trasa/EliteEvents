namespace EliteEvents.Eddn.Config;

public class EddnOptions
{
    public string StreamUrl { get; set; } = "tcp://eddn.edcd.io:9500";
    public int ReceiveHighWatermark { get; set; } = 1000;

    /// <summary>
    /// How long a single <see cref="EddnStream.Receive"/> waits for a frame before
    /// returning null. Bounds the blocking receive so the loop can wake during silence
    /// and evaluate whether to reconnect.
    /// </summary>
    public TimeSpan ReceiveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// If no EDDN message has arrived for this long, the receiver tears down and rebuilds
    /// the socket. Kept below the stream health-check threshold (5 min) so automatic
    /// recovery is attempted before the Unhealthy alert fires.
    /// </summary>
    public TimeSpan ReconnectAfterSilence { get; set; } = TimeSpan.FromMinutes(2);
}
