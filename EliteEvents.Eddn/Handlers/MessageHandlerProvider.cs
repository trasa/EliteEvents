namespace EliteEvents.Eddn.Handlers;

public interface IMessageHandlerProvider<in TMessage, TMessageEvent>
    where TMessage : IEddnMessage
    where TMessageEvent : Enum
{
    IEnumerable<IMessageHandler<TMessage, TMessageEvent>> GetMessageHandlers(TMessageEvent messageEvent);
}

public class MessageHandlerProvider<TMessage, TMessageEvent> : IMessageHandlerProvider<TMessage, TMessageEvent>
    where TMessage : IEddnMessage
    where TMessageEvent : Enum
{
    private readonly Dictionary<TMessageEvent, List<IMessageHandler<TMessage, TMessageEvent>>> _eventHandlers = new();

    public MessageHandlerProvider(IEnumerable<IMessageHandler<TMessage, TMessageEvent>> handlers)
    {
        foreach (var h in handlers)
        {
            foreach (var key in h.Handles)
            {
                if (_eventHandlers.TryGetValue(key, out var list))
                {
                    list.Add(h);
                }
                else
                {
                    _eventHandlers.Add(key, [h]);
                }
            }
        }
    }

    public IEnumerable<IMessageHandler<TMessage, TMessageEvent>> GetMessageHandlers(TMessageEvent messageEvent)
    {
        return _eventHandlers.TryGetValue(messageEvent, out var list) ? list : [];
    }
}
