using EliteEvents.Eddn.Storage;
using EliteEvents.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace EliteEvents.Web.Services;

/// <summary>
/// Renders a single live-ticker row to HTML on the server, so the SSE stream can push markup
/// straight into the page and htmx needs no client-side templating.
/// <para>
/// The point of going through <see cref="TickerItem"/> rather than concatenating a string is that
/// the same component renders the rows the page ships with on first load, so there is exactly one
/// definition of what a ticker row looks like.
/// </para>
/// </summary>
public sealed class TickerFragmentRenderer : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly HtmlRenderer _renderer;

    public TickerFragmentRenderer(IServiceScopeFactory scopeFactory, ILoggerFactory loggerFactory)
    {
        // HtmlRenderer resolves scoped component services, so it can't be built from the root
        // provider. This one scope lives as long as the app: ticker rows inject nothing, and a
        // scope per event would be pure churn at firehose rates.
        _scope = scopeFactory.CreateAsyncScope();
        _renderer = new HtmlRenderer(_scope.ServiceProvider, loggerFactory);
    }

    public Task<string> RenderAsync(LiveEvent liveEvent)
    {
        // Every render has to run on the renderer's dispatcher; it is a single logical thread, and
        // ticker rows are tiny, so this is not a throughput concern at EDDN's event rate.
        return _renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TickerItem.Event)] = liveEvent
            });
            var output = await _renderer.RenderComponentAsync<TickerItem>(parameters);
            return output.ToHtmlString();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _scope.DisposeAsync();
    }
}
