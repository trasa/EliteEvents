namespace EliteEvents.Eddn.Handlers;

public interface IMessageHandler<in TMessage, out TMessageEvent>
    where TMessage : IEddnMessage
    where TMessageEvent : Enum
{
    TMessageEvent[] Handles { get; }

    Task<bool> Handle(TMessage message);
}
