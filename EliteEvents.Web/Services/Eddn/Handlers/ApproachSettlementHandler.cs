using Newtonsoft.Json.Linq;

namespace EliteEvents.Web.Services.Eddn.Handlers;


public class ApproachSettlementHandler : IEddnHandler
{
    public string Schema => "https://eddn.edcd.io/schemas/approachsettlement/1";

    public Task Handle(JToken token)
    {
        throw new NotImplementedException();
    }
}
