using EliteEvents.Eddn.ApproachSettlement;
using EliteEvents.Eddn.Journal;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Eddn;

public interface IMessageFactory
{
    IEddnMessage? Create(JToken token);
}

public class MessageFactory : IMessageFactory
{
    private readonly Dictionary<string, Type> _schemaToType = new()
    {
        ["https://eddn.edcd.io/schemas/journal/1"] = typeof(JournalMessage),
        ["https://eddn.edcd.io/schemas/approachsettlement/1"] = typeof(ApproachSettlementMessage),
    };

    public IEddnMessage? Create(JToken token)
    {
        var schema = token["$schemaRef"]?.Value<string>();
        if (!string.IsNullOrEmpty(schema) && _schemaToType.TryGetValue(schema, out var type))
        {
            return (IEddnMessage?)token.ToObject(type);
        }

        return null;
    }
}
