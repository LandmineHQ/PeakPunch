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
        Character interactor = Character.localCharacter;
        if (IsBuddyClimbDropInteraction(__instance.character, interactor))
        {
            __result = string.Empty;
            return;
        }

        if (__result != string.Empty)
        {
            return;
        }

        if (CanStartClimb(__instance.character, interactor))
        {
            BuddyClimbTextKey textKey = BackpackCarryTransfer.WillDropCarriedBackpack(
                __instance.character,
                interactor)
                ? BuddyClimbTextKey.ClimbOnTeammateDropBackpack
                : BuddyClimbTextKey.ClimbOnTeammate;

            __result = BuddyClimbLocalization.Get(textKey);
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.Interact))]
    [HarmonyPrefix]
    private static bool InteractPatch(CharacterInteractible __instance, ref Character interactor)
    {
        if (IsBuddyClimbDropInteraction(__instance.character, interactor))
        {
            return false;
        }

        if (__instance.CarriedByLocalCharacter() || __instance.IsCannibal() || __instance.CanBeCarried())
        {
            return true;
        }

        return !TryStartClimb(__instance.character, interactor);
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsInteractible))]
    [HarmonyPostfix]
    private static void IsInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (IsBuddyClimbDropInteraction(__instance.character, interactor))
        {
            __result = false;
            return;
        }

        if (__result)
        {
            return;
        }

        if (CanStartClimb(__instance.character, interactor))
        {
            __result = true;
        }
    }

    [HarmonyPatch(nameof(CharacterInteractible.IsPrimaryInteractible))]
    [HarmonyPostfix]
    private static void IsPrimaryInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
    {
        if (IsBuddyClimbDropInteraction(__instance.character, interactor))
        {
            __result = false;
            return;
        }

        if (__result)
        {
            return;
        }

        if (CanStartClimb(__instance.character, interactor))
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

        if (character.data.IsCarryingCharacter)
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

        if (character.data.isCarried)
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

    private static bool TryStartClimb(Character character, Character interactor)
    {
        if (!CanStartClimb(character, interactor))
        {
            return false;
        }

        BackpackPreparationResult preparationResult = BackpackCarryTransfer.PrepareBackpacksForClimb(
            character,
            interactor);
        if (preparationResult == BackpackPreparationResult.Failed)
        {
            return true;
        }

        if (preparationResult == BackpackPreparationResult.Ready)
        {
            BuddyClimbCarryStarter.TryStartCarry(character, interactor);
        }

        return true;
    }

    private static bool CanStartClimb(Character carrier, Character carried)
    {
        return CanBeClimbed(carrier)
            && CanClimb(carried)
            && BuddyClimbCarryStarter.CanCreateCarryLink(carrier, carried);
    }

    private static bool IsBuddyClimbDropInteraction(Character character, Character interactor)
    {
        return character != null
            && interactor != null
            && character.data.carrier == interactor
            && CharacterCarryingPatch.IsBuddyClimbCarried(character);
    }

}
