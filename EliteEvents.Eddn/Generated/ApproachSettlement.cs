using EliteEvents.Eddn.Handlers;

// ReSharper disable once CheckNamespace
namespace EliteEvents.Eddn.ApproachSettlement;

public partial class ApproachSettlementMessage : IEddnMessage
{

}


public interface IApproachSettlementMessageHandler : IMessageHandler<ApproachSettlementMessage, MessageEvent>
{
}
