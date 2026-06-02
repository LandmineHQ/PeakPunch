using HarmonyLib;
using Photon.Pun;
using BuddyClimb.Gameplay;
using BuddyClimb.Localization;

namespace BuddyClimb.Patches;

[HarmonyPatch(typeof(CharacterInteractible))]
internal static class CharacterInteractiblePatch
{
    [HarmonyPatch(nameof(CharacterInteractible.GetInteractionText))]
    [HarmonyPostfix]
    private static void GetInteractionTextPatch(ref string __result, CharacterInteractible __instance)
    {
        if (__result != string.Empty)
        {
            return;
        }

        if (CanBeClimbed(__instance.character))
        {
            BuddyClimbTextKey textKey = BackpackCarryTransfer.WillDropCarriedBackpack(
                __instance.character,
                Character.localCharacter)
                ? BuddyClimbTextKey.ClimbOnTeammateDropBackpack
                : BuddyClimbTextKey.ClimbOnTeammate;

            __result = BuddyClimbLocalization.Get(textKey);
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.Interact))]
    [HarmonyPostfix]
    private static void InteractPatch(CharacterInteractible __instance, ref Character interactor)
    {
        if (__instance.CarriedByLocalCharacter() || (!__instance.IsCannibal() && __instance.CanBeCarried()))
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            if (!BackpackCarryTransfer.TryTransferCarrierBackpack(__instance.character, interactor))
            {
                return;
            }

            __instance.character.photonView.RPC(
                nameof(CharacterCarrying.RPCA_StartCarry),
                RpcTarget.All,
                interactor.photonView);
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsInteractible))]
    [HarmonyPostfix]
    private static void IsInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (__result)
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            __result = true;
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsPrimaryInteractible))]
    [HarmonyPostfix]
    private static void IsPrimaryInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (__result)
        {
            return;
        }

        if (CanBeClimbed(__instance.character) && CanClimb(interactor))
        {
            __result = true;
        }
    }

    private static bool CanBeClimbed(Character character)
    {
        if (character == null)
        {
            return false;
        }

        if (character.isBot)
        {
            return false;
        }

        if (character.IsLocal)
        {
            return false;
        }

        if (character.data.dead)
        {
            return false;
        }

        if (character.player.backpackSlot.hasBackpack && !BackpackCarryTransfer.AllowsCarrierBackpack)
        {
            return false;
        }

        if (IsCharacterDoingIllegalCarryActions(character))
        {
            return false;
        }

        if (character.data.carriedPlayer)
        {
            return false;
        }

        if (character.data.carrier)
        {
            return false;
        }

        if (character.data.currentItem && character.data.currentItem.canUseOnFriend)
        {
            return false;
        }

        if (character.refs.customization.isCannibalizable)
        {
            return false;
        }

        return true;
    }

    private static bool CanClimb(Character character)
    {
        if (character == null)
        {
            return false;
        }

        if (character.refs.interactible.CanBeCarried())
        {
            return true;
        }

        if (!character.IsLocal)
        {
            return false;
        }

        if (character.data.dead)
        {
            return false;
        }

        if (character.data.currentItem)
        {
            return false;
        }

        if (character.data.carrier)
        {
            return false;
        }

        if (character.refs.customization.isCannibalizable)
        {
            return false;
        }

        return true;
    }

    private static bool IsCharacterDoingIllegalCarryActions(Character character)
    {
        return character.data.isSprinting
            || character.data.isJumping
            || character.data.isClimbingAnything
            || character.data.isCrouching
            || character.data.isReaching;
    }
}
