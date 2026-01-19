namespace EliteEvents.Eddn.Config;

public class EddnOptions
{
    public string StreamUrl { get; set; } = "tcp://eddn.edcd.io:9500";
    public int ReceiveHighWatermark { get; set; } = 1000;
}
