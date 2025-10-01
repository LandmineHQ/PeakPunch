using HarmonyLib;
using Photon.Pun;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.TextCore.Text;

namespace PeakPunch.patches
{
    [HarmonyPatch(typeof(CharacterInteractible))]
    internal class CharacterInteractiblePatch
    {

        [HarmonyPatch(nameof(CharacterInteractible.GetInteractionText))]
        [HarmonyPostfix]
        private static void GetInteractionTextPatch(ref string __result, CharacterInteractible __instance)
        {
            if (__result != "")
                return;

            if (CanBeClimbed(__instance.character))
                __result = "爬上去!";//the string "Climb on!"
        }

        [HarmonyPatch(nameof(CharacterInteractible.Interact))]
        [HarmonyPostfix]
        private static void InteractPatch(CharacterInteractible __instance, ref Character interactor)
        {
            if (__instance.CarriedByLocalCharacter() ||
                (!__instance.IsCannibal() && __instance.CanBeCarried()))
                return;

            if(CanBeClimbed(__instance.character) && CanClimb(interactor))
            {
                interactor.photonView.RPC("RPCA_PassOut", __instance.character.player.photonView.Owner);
                __instance.character.refs.carriying.StartCarry(interactor);
            }
        }

        [HarmonyPatch(nameof(CharacterInteractible.IsPrimaryInteractible))]
        [HarmonyPostfix]
        private static void IsPrimaryInteractiblePatch(CharacterInteractible __instance, ref bool __result, ref Character interactor)
        {
            if (__result) return ;

            if (CanBeClimbed(__instance.character) && CanClimb(interactor))
            {
                __result = true;
            }
        }

        private static bool CanBeClimbed(Character ___character)
        {
            if (___character.IsLocal) return false;
            if (___character.data.dead) return false;
            if (___character.player.backpackSlot.hasBackpack) return false;
            if (IsCharacterDoingIllegalCarryActions(___character)) return false;
            if (___character.data.carriedPlayer) return false;
            if (___character.data.carrier) return false;
            if (___character.data.currentItem &&
                ___character.data.currentItem.canUseOnFriend) return false;
            if (___character.refs.customization.isCannibalizable) return false;

            return true;
        }

        private static bool CanClimb(Character ___character)
        {
            if (___character.refs.interactible.CanBeCarried()) return true;
            if (!___character.IsLocal) return false;
            if (___character.data.dead) return false;
            if (___character.data.currentItem) return false;
            if (___character.data.carrier) return false;
            if (___character.data.currentItem &&
                ___character.data.currentItem.canUseOnFriend) return false;
            if (___character.refs.customization.isCannibalizable) return false;

            return true;
        }

        [HarmonyPatch(typeof(CharacterCarrying),nameof(CharacterCarrying.RPCA_Drop))]
        [HarmonyPrefix]
        private static bool RPCA_DropPrefix(PhotonView targetView)
        {
            var character = targetView.GetComponent<Character>();

            if ( ! targetView.IsMine )
                return true;

            if ( ! targetView.GetComponent<Character>().data.fullyPassedOut)
            {
                targetView.RPC("RPCA_UnPassOut", character.data.carrier.photonView.Owner);
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
}
