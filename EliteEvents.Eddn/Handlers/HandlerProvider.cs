using System.Collections.ObjectModel;
using Newtonsoft.Json.Linq;

namespace EliteEvents.Eddn.Handlers;

public interface IHandlerProvider
{
    IEddnHandler? FindHandler(JToken message);
    IEddnHandler? FindHandler(string? schema);
}

public class HandlerProvider : IHandlerProvider
{
    private readonly ReadOnlyDictionary<string, IEddnHandler> _schemas;

    public HandlerProvider(IEnumerable<IEddnHandler> handlers)
    {
        _schemas = new ReadOnlyDictionary<string, IEddnHandler>(handlers.ToDictionary(h => h.Schema, h => h));
    }

    public IEddnHandler? FindHandler(JToken message)
    {
        return FindHandler(message["$schemaRef"]?.Value<string>());
    }

    public IEddnHandler? FindHandler(string? schema)
    {
        return string.IsNullOrEmpty(schema) ? null : _schemas.GetValueOrDefault(schema);
    }
}
