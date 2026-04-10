namespace Seeing.Agent.Tui.Core
{
    public enum RenderRegion
    {
        Header,
        Messages,
        Streaming,
        Input
    }

    public class StateChangedEvent : EventArgs
    {
        public RenderRegion Region { get; }
        public StateChangedEvent(RenderRegion region)
        {
            Region = region;
        }
    }
}
