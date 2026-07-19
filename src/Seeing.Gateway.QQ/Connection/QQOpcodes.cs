namespace Seeing.Gateway.QQ.Connection;

public static class QQOpcodes
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
}

public static class QQIntents
{
    public const int GuildMembers = 1 << 1;
    public const int PublicGuildMessages = 1 << 30;
    public const int DirectMessage = 1 << 12;
    public const int GroupAndC2C = 1 << 25;
    public const int Interaction = 1 << 26;

    public const int Default =
        PublicGuildMessages | GuildMembers | DirectMessage | GroupAndC2C | Interaction;
}
