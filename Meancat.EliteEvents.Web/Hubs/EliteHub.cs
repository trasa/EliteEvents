using Microsoft.AspNetCore.SignalR;

namespace Meancat.EliteEvents.Web.Hubs;

public class EliteHub : Hub
{
    public async Task SubscribeToEvents()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, nameof(EliteHub));
    }

}
