using BuddyClimb.Patches;
using UnityEngine;

namespace BuddyClimb.Gameplay;

internal static class CarriedPlayerDropper
{
    private static int dropInputConsumedFrame = -1;

    internal static void Update()
    {
        if (dropInputConsumedFrame == Time.frameCount || !Input.GetKeyDown(KeyCode.Space))
        {
            return;
        }

        TryDropLocalPlayer(Character.localCharacter);
    }

    internal static bool HandleJumpAttempt(Character character)
    {
        if (character == null || !character.IsLocal)
        {
            return false;
        }

        if (dropInputConsumedFrame == Time.frameCount)
        {
            ClearJumpInput(character);
            return true;
        }

        return TryDropLocalPlayer(character);
    }

    private static bool TryDropLocalPlayer(Character localCharacter)
    {
        if (!CanRequestDrop(localCharacter))
        {
            return false;
        }

        CharacterCarrying carrierCarrying = localCharacter.data.carrier.refs.carriying;
        if (carrierCarrying == null)
        {
            return false;
        }

        ClearJumpInput(localCharacter);
        carrierCarrying.Drop(localCharacter);
        ClearJumpInput(localCharacter);
        dropInputConsumedFrame = Time.frameCount;

        return true;
    }

    private static bool CanRequestDrop(Character character)
    {
        return character != null
            && character.data.isCarried
            && character.data.carrier != null
            && !character.data.dead
            && !character.data.passedOut
            && !character.data.fullyPassedOut
            && CharacterCarryingPatch.IsBuddyClimbCarried(character);
    }

    private static void ClearJumpInput(Character character)
    {
        if (character.input == null)
        {
            return;
        }

        character.input.jumpWasPressed = false;
        character.input.jumpIsPressed = false;
    }
}
