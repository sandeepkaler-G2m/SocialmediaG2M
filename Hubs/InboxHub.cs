using Microsoft.AspNetCore.SignalR;

namespace SocialMediaPanel.Hubs
{
    /// <summary>
    /// Push channel for the Inbox. Deliberately payload-less — clients just
    /// re-fetch (list + whichever thread is open) on the "inboxChanged" event
    /// rather than trying to reconcile a partial server-sent object client-side.
    /// This single app/team's traffic is low enough that a broadcast-to-everyone
    /// model is simpler and safer than per-user/per-page SignalR groups.
    /// </summary>
    public class InboxHub : Hub
    {
    }
}
