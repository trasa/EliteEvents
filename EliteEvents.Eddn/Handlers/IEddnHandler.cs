using Newtonsoft.Json.Linq;

namespace EliteEvents.Eddn.Handlers;

public interface IEddnHandler
{
    string Schema { get; }

    Task Handle(JToken token);
}
