using Photon.Pun;

namespace BuddyClimb.Gameplay;

internal static class BuddyClimbCarryStarter
{
    internal static bool TryStartCarry(Character carrier, Character carried)
    {
        BuddyClimbDiagnostics.LogCarry($"TryStartCarry requested: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");

        if (!CanStartCarryRpc(carrier, carried))
        {
            BuddyClimbDiagnostics.LogCarry($"TryStartCarry blocked by CanStartCarryRpc=false: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return false;
        }

        BuddyClimbDiagnostics.LogCarry(
            $"Sending Photon RPC {nameof(CharacterCarrying.RPCA_StartCarry)} target=All carrierView={carrier.photonView.ViewID} carriedView={carried.photonView.ViewID}: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");

        BuddyClimbRemotePassOutSync.PrepareRemoteCarryStart(carried);

        carrier.photonView.RPC(
            nameof(CharacterCarrying.RPCA_StartCarry),
            RpcTarget.All,
            carried.photonView);

        BuddyClimbDiagnostics.LogCarry(
            $"Sent Photon RPC {nameof(CharacterCarrying.RPCA_StartCarry)} carrierView={carrier.photonView.ViewID} carriedView={carried.photonView.ViewID}");

        return true;
    }

    internal static bool CanCreateCarryLink(Character carrier, Character carried)
    {
        if (carrier == null || carried == null)
        {
            return false;
        }

        Character currentCarrier = carrier;
        while (true)
        {
            if (currentCarrier == carried)
            {
                return false;
            }

            if (currentCarrier.data?.carrier is not Character nextCarrier)
            {
                return true;
            }

            currentCarrier = nextCarrier;
        }
    }

    private static bool CanStartCarryRpc(Character carrier, Character carried)
    {
        if (carrier == null
            || carried == null
            || carrier == carried
            || carrier.photonView == null
            || carried.photonView == null)
        {
            BuddyClimbDiagnostics.LogCarry($"CanStartCarryRpc basic validation failed: {BuddyClimbDiagnostics.DescribeViews(carrier, carried)}");
            return false;
        }

        if (!CanCreateCarryLink(carrier, carried))
        {
            Plugin.Log.LogDebug($"Skipping BuddyClimb start carry because it would create a carry cycle between {carrier.characterName} and {carried.characterName}.");
            return false;
        }

        if (carrier.data.dead || carried.data.dead || carried.data.fullyPassedOut)
        {
            Plugin.Log.LogDebug($"Skipping BuddyClimb start carry for {carried.characterName} because the carry state is no longer valid.");
            return false;
        }

        return true;
    }
}
