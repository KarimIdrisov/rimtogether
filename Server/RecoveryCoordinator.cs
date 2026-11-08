using Multiplayer.Common;

namespace RimTogether;

public static class RecoveryCoordinator
{
    // A live Unity world is captured only when a participant actually needs a new base.
    // Periodic server persistence uses the existing immutable world bytes and command journal.
    public const AutoJoinPointFlags AutomaticTriggers = AutoJoinPointFlags.Join | AutoJoinPointFlags.Desync;
}
