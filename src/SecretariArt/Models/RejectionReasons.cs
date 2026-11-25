namespace SecretariArt.Models;

// Every rejection (of a join request or of an organisation request) carries a reason the requester can read.
public static class RejectionReasons
{
    public const int MaxLength = 500;

    // After a rejection, the same person can ask to join the same organisation (in the same role) again only after
    // this wait: long enough to stop instant re-asking, short enough not to punish a genuine mistake.
    public static readonly TimeSpan RejoinWait = TimeSpan.FromMinutes(10);
}
