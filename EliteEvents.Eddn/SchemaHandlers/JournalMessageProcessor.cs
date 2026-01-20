using EliteEvents.Eddn.Journal;

namespace EliteEvents.Eddn.SchemaHandlers;

public interface IMessageProcessor<in TMessage> where TMessage : IEddnMessage
{
    string[] HandlesEventTypes { get; }
    Task<bool> Process(TMessage message);
}

public interface IJournalMessageProcessor : IMessageProcessor<JournalMessage>
{

}
