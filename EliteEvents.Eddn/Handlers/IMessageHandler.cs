namespace EliteEvents.Eddn.Handlers;

/// <summary>
/// Implementation that does "something" with a message of type TMessage
/// </summary>
/// <typeparam name="TMessage"></typeparam>
/// <typeparam name="TMessageEvent"></typeparam>
public interface IMessageHandler<in TMessage, out TMessageEvent>
    where TMessage : IEddnMessage
    where TMessageEvent : Enum
{
    /// <summary>
    /// The message Events (enum) that this processes.
    /// </summary>
    TMessageEvent[] Handles { get; }

    /// <summary>
    /// Process the message
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    Task Handle(TMessage message);
}
