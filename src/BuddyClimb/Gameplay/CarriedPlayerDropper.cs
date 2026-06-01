using UnityEngine;

namespace BuddyClimb.Gameplay;

internal static class CarriedPlayerDropper
{
    internal static void Update()
    {
        Character localCharacter = Character.localCharacter;
        if (localCharacter == null
            || !localCharacter.data.isCarried
            || localCharacter.data.carrier == null
            || !Input.GetKeyDown(KeyCode.Space))
        {
            return;
        }

        localCharacter.data.carrier.refs.carriying.Drop(localCharacter);
    }
}
