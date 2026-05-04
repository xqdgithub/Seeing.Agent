namespace Seeing.Session.Hooks;

public static class SessionHookPoints
{
    public const string Created = "session.created";
    public const string Saving = "session.saving";
    public const string Saved = "session.saved";
    public const string Loading = "session.loading";
    public const string Loaded = "session.loaded";
    public const string Destroyed = "session.destroyed";
}