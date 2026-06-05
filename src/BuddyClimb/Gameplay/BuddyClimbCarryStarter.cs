using Photon.Pun;

namespace BuddyClimb.Gameplay;

internal static class BuddyClimbCarryStarter
{
    internal static bool TryStartCarry(Character carrier, Character carried)
    {
        if (!CanStartCarryRpc(carrier, carried))
        {
            return false;
        }

        carrier.photonView.RPC(
            nameof(CharacterCarrying.RPCA_StartCarry),
            RpcTarget.All,
            carried.photonView);

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

        Character existingCarriedPlayer = carrier.data.carriedPlayer;
        if (existingCarriedPlayer != null && existingCarriedPlayer != carried)
        {
            Plugin.Log.LogDebug($"Skipping BuddyClimb start carry because {carrier.characterName} is already carrying {existingCarriedPlayer.characterName}.");
            return false;
        }

        if (carried.data.isCarried && carried.data.carrier != carrier)
        {
            Plugin.Log.LogDebug($"Skipping BuddyClimb start carry because {carried.characterName} is already carried by another player.");
            return false;
        }

        return true;
    }
}
