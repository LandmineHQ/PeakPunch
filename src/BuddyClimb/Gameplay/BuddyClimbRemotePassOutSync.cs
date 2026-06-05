using System.Collections.Generic;
using Photon.Pun;

namespace BuddyClimb.Gameplay;

internal static class BuddyClimbRemotePassOutSync
{
    private static readonly HashSet<int> RemotePassOutViewIds = [];

    internal static void PrepareRemoteCarryStart(Character carried)
    {
        if (!CanSyncLocalConsciousCharacter(carried))
        {
            BuddyClimbDiagnostics.LogCarry($"Skipping remote-only pass-out sync before carry start: {BuddyClimbDiagnostics.Describe(carried)}");
            return;
        }

        int viewId = carried.photonView.ViewID;
        RemotePassOutViewIds.Add(viewId);

        BuddyClimbDiagnostics.LogCarry($"Sending remote-only {nameof(Character.RPCA_PassOut)} before carry start: {BuddyClimbDiagnostics.Describe(carried)}");
        carried.photonView.RPC(nameof(Character.RPCA_PassOut), RpcTarget.Others);
    }

    internal static void RestoreRemoteCarryDrop(Character carried)
    {
        if (carried?.photonView == null)
        {
            return;
        }

        int viewId = carried.photonView.ViewID;
        if (!RemotePassOutViewIds.Remove(viewId))
        {
            return;
        }

        if (!CanSyncLocalConsciousCharacter(carried))
        {
            BuddyClimbDiagnostics.LogCarry($"Skipping remote-only un-pass-out sync after carry drop: {BuddyClimbDiagnostics.Describe(carried)}");
            return;
        }

        BuddyClimbDiagnostics.LogCarry($"Sending remote-only {nameof(Character.RPCA_UnPassOut)} after carry drop: {BuddyClimbDiagnostics.Describe(carried)}");
        carried.photonView.RPC(nameof(Character.RPCA_UnPassOut), RpcTarget.Others);
    }

    private static bool CanSyncLocalConsciousCharacter(Character character)
    {
        return character != null
            && character.IsLocal
            && character.photonView != null
            && character.data != null
            && !character.data.dead
            && !character.data.passedOut
            && !character.data.fullyPassedOut;
    }
}
