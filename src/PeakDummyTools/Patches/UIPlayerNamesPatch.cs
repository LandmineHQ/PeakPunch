using HarmonyLib;
using PeakDummyTools.DummyPlayers;
using UnityEngine;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(UIPlayerNames))]
internal static class UIPlayerNamesPatch
{
    [HarmonyPatch(nameof(UIPlayerNames.UpdateName))]
    [HarmonyPrefix]
    private static bool UpdateNamePrefix(
        UIPlayerNames __instance,
        int index,
        Vector3 position,
        bool visible,
        int speakingAmplitude)
    {
        if (index < 0 || index >= __instance.playerNameText.Length)
        {
            return false;
        }

        PlayerName playerName = __instance.playerNameText[index];
        Character? character = playerName.characterInteractable?.character;
        if (character == null || !DummyPlayerSpawner.TryGetDummyPlayerName(character, out string dummyName))
        {
            return true;
        }

        if (!Character.localCharacter)
        {
            return false;
        }

        playerName.text.text = dummyName;
        playerName.transform.position = MainCamera.instance.cam.WorldToScreenPoint(position);
        if (visible)
        {
            playerName.gameObject.SetActive(true);
            playerName.group.alpha = Mathf.MoveTowards(playerName.group.alpha, 1f, Time.deltaTime * 5f);
            if (playerName.audioImage != null && __instance.audioSprites.Length > 0)
            {
                playerName.audioImage.sprite = __instance.audioSprites[0];
            }

            playerName.audioImageTimeout = 0f;
            playerName.hostStar.SetActive(false);
            return false;
        }

        playerName.group.alpha = Mathf.MoveTowards(playerName.group.alpha, 0f, Time.deltaTime * 5f);
        if (playerName.group.alpha < 0.01f && playerName.gameObject.activeSelf)
        {
            character.refs.customization.BecomeHuman();
            playerName.gameObject.SetActive(false);
        }

        return false;
    }
}
