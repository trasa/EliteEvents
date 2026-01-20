
using EliteEvents.Eddn.Handlers;


// ReSharper disable once CheckNamespace
namespace EliteEvents.Eddn.Journal;

public partial class JournalMessage : IEddnMessage {}

public interface IJournalMessageHandler : IMessageHandler<JournalMessage, MessageEvent>
{
}
