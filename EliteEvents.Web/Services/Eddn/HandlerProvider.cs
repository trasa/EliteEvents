using System.Collections.ObjectModel;
using EliteEvents.Web.Services.Eddn.Handlers;

namespace EliteEvents.Web.Services.Eddn;

public class HandlerProvider
{
    private readonly ReadOnlyDictionary<string, IEddnHandler> _schemas;

    public HandlerProvider(IEnumerable<IEddnHandler> handlers)
    {
        _schemas = new ReadOnlyDictionary<string, IEddnHandler>(handlers.ToDictionary(h => h.Schema, h => h));
    }

    public IEddnHandler? GetHandler(string? schema)
    {
        return string.IsNullOrEmpty(schema) ? null : _schemas.GetValueOrDefault(schema);
    }
}
