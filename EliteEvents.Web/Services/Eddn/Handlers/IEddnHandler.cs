using Newtonsoft.Json.Linq;

namespace EliteEvents.Web.Services.Eddn.Handlers;

public interface IEddnHandler
{
    string Schema { get; }

    Task Handle(JToken token);
}
