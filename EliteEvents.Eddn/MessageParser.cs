using Newtonsoft.Json.Linq;

namespace EliteEvents.Eddn;

public interface IMessageParser
{

    JToken Parse(string msg);
}
public class MessageParser : IMessageParser
{
    public JToken Parse(string msg)
    {
        return JToken.Parse(msg);
    }
}
