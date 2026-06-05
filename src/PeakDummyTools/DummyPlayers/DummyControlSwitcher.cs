using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using PeakDummyTools.Configuration;
using PeakDummyTools.Localization;
using Photon.Pun;
using Photon.Voice.Unity;
using UnityEngine;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlSwitcher
{
    private const float FallbackSwitchTargetDistance = 4f;
    private const float FallbackSwitchTargetAngle = 35f;
    private const float TargetDistanceScoreWeight = 0.02f;
    private const int LineOfSightHitBufferSize = 16;

    private static readonly MethodInfo ResetInputMethod = AccessTools.Method(typeof(CharacterInput), "ResetInput");
    private static readonly MethodInfo SetSpecCharacterMethod = AccessTools.PropertySetter(
        typeof(MainCameraMovement),
        nameof(MainCameraMovement.specCharacter));
    private static readonly RaycastHit[] LineOfSightHits = new RaycastHit[LineOfSightHitBufferSize];

    private static Character? originalLocalCharacter;
    private static Character? controlledCharacter;

    internal static void Update()
    {
        if (!CanUseDummyControl())
        {
            RestoreOriginalLocalControlIfNeeded();
            return;
        }

        if (controlledCharacter != null && !CanControlCharacter(controlledCharacter))
        {
            RestoreOriginalLocalControlIfNeeded();
        }

        CaptureOriginalLocalCharacterIfAvailable();

        if (PeakDummyToolsConfig.DeleteDummyShortcut.Value.IsDown())
        {
            DeleteFromShortcut();
            return;
        }

        if (!PeakDummyToolsConfig.SwitchControlShortcut.Value.IsDown())
        {
            return;
        }

        SwitchFromShortcut();
    }

    internal static bool TryGetControlledCharacterForLocalPlayer(Player player, out Character character)
    {
        character = null!;
        if (player == null || Player.localPlayer == null || player != Player.localPlayer)
        {
            return false;
        }

        if (controlledCharacter == null || !CanControlCharacter(controlledCharacter))
        {
            return false;
        }

        character = controlledCharacter;
        return true;
    }

    internal static bool TryGetSwitchPrompt(out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;

        if (!TryGetSwitchTarget(out Character target))
        {
            return false;
        }

        return TryGetSwitchPrompt(target, out keyText, out text);
    }

    internal static bool TryGetCurrentHoveredSwitchPrompt(out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;

        Interaction interaction = Interaction.instance;
        if (interaction?.currentHovered is not CharacterInteractible characterInteractible)
        {
            return false;
        }

        return TryGetSwitchPrompt(characterInteractible.character, out keyText, out text);
    }

    internal static bool TryGetCurrentHoveredDeletePrompt(out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;

        Interaction interaction = Interaction.instance;
        if (interaction?.currentHovered is not CharacterInteractible characterInteractible)
        {
            return false;
        }

        return TryGetDeletePrompt(characterInteractible.character, out keyText, out text);
    }

    internal static bool CanShowHoveredPrompt(Character character)
    {
        return CanShowSwitchPrompt(character) || CanDeleteDummy(character);
    }

    internal static bool CanShowSwitchPrompt(Character character)
    {
        if (!CanUseDummyControl() || character == null || character == Character.localCharacter)
        {
            return false;
        }

        CaptureOriginalLocalCharacterIfAvailable();

        return CanControlCharacter(character);
    }

    internal static bool CanDeleteDummy(Character character)
    {
        return CanUseDummyControl()
            && character != null
            && character != Character.localCharacter
            && character.photonView != null
            && character.photonView.IsMine
            && DummyPlayerSpawner.IsLocallySpawnedDummyPlayer(character);
    }

    internal static void HandleCharacterRemoved(Character character)
    {
        if (character == null)
        {
            return;
        }

        DummyControlPhotonViewAuthority.HandleCharacterRemoved(character);

        if (character == originalLocalCharacter)
        {
            originalLocalCharacter = null;
        }

        if (character != controlledCharacter)
        {
            return;
        }

        controlledCharacter = null;
        if (TryFindOriginalLocalCharacter(out Character originalLocal))
        {
            AssignLocalControl(originalLocal);
            Plugin.Log.LogInfo("Restored local control because the controlled character was removed.");
        }
    }

    internal static bool IsControllingTarget(Character character)
    {
        return character != null
            && controlledCharacter == character
            && Character.localCharacter == character;
    }

    private static bool TryGetSwitchPrompt(Character character, out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;
        if (!CanShowSwitchPrompt(character))
        {
            return false;
        }

        PeakDummyToolsTextKey textKey = IsControllingAlternate()
            && originalLocalCharacter != null
            && character == originalLocalCharacter
                ? PeakDummyToolsTextKey.SwitchControlToOriginal
                : PeakDummyToolsTextKey.SwitchControlToDummy;
        keyText = GetShortcutText(PeakDummyToolsConfig.SwitchControlShortcut.Value);
        text = PeakDummyToolsLocalization.Get(textKey);
        return true;
    }

    private static bool TryGetDeletePrompt(Character character, out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;
        if (!CanDeleteDummy(character))
        {
            return false;
        }

        keyText = GetShortcutText(PeakDummyToolsConfig.DeleteDummyShortcut.Value);
        text = PeakDummyToolsLocalization.Get(PeakDummyToolsTextKey.DeleteDummy);
        return true;
    }

    private static void DeleteFromShortcut()
    {
        if (!TryGetDeleteTarget(out Character target))
        {
            if (IsHoveringAnyDummy())
            {
                Plugin.Log.LogWarning("The targeted dummy was not generated by this client and cannot be deleted.");
                return;
            }

            if (DummyPlayerSpawner.TryDestroyQueuedDummyPlayer())
            {
                return;
            }

            Plugin.Log.LogWarning("No deletable dummy target is currently selected and no generated dummy remains in the delete queue.");
            return;
        }

        DummyPlayerSpawner.TryDestroyDummyPlayer(target);
    }

    private static void SwitchFromShortcut()
    {
        if (TryGetSwitchTarget(out Character target))
        {
            SwitchControl(target);
            return;
        }

        if (IsControllingAlternate())
        {
            RestoreOriginalLocalControlIfNeeded();
            return;
        }

        Plugin.Log.LogWarning("No switchable dummy target is currently selected.");
    }

    private static bool TryGetSwitchTarget(out Character target)
    {
        target = null!;

        if (!CanUseDummyControl())
        {
            return false;
        }

        Interaction interaction = Interaction.instance;
        if (interaction?.currentHovered is CharacterInteractible characterInteractible
            && CanShowSwitchPrompt(characterInteractible.character))
        {
            target = characterInteractible.character;
            return true;
        }

        return TryFindLookTarget(out target);
    }

    private static bool TryGetDeleteTarget(out Character target)
    {
        target = null!;

        if (!CanUseDummyControl())
        {
            return false;
        }

        Interaction interaction = Interaction.instance;
        if (interaction?.currentHovered is CharacterInteractible characterInteractible
            && CanDeleteDummy(characterInteractible.character))
        {
            target = characterInteractible.character;
            return true;
        }

        return TryFindLookTarget(out target, CanDeleteDummy);
    }

    private static bool IsHoveringAnyDummy()
    {
        Interaction interaction = Interaction.instance;
        return interaction?.currentHovered is CharacterInteractible characterInteractible
            && DummyPlayerSpawner.IsDummyPlayer(characterInteractible.character);
    }

    private static bool TryFindLookTarget(out Character target)
    {
        return TryFindLookTarget(out target, CanShowSwitchPrompt);
    }

    private static bool TryFindLookTarget(out Character target, System.Func<Character, bool> canUseCandidate)
    {
        target = null!;
        Character localCharacter = Character.localCharacter;
        MainCamera mainCamera = MainCamera.instance;
        if (localCharacter == null || mainCamera == null)
        {
            return false;
        }

        Transform cameraTransform = mainCamera.transform;
        Vector3 origin = cameraTransform.position;
        Vector3 forward = cameraTransform.forward;
        float maxDistance = GetSwitchTargetDistance();
        float maxAngle = GetSwitchTargetAngle(maxDistance);
        float bestScore = float.MaxValue;

        foreach (Character candidate in Character.AllCharacters)
        {
            if (!canUseCandidate(candidate))
            {
                continue;
            }

            Vector3 candidateCenter = GetCharacterCenter(candidate);
            Vector3 offset = candidateCenter - origin;
            float distance = offset.magnitude;
            if (distance <= Mathf.Epsilon || distance > maxDistance)
            {
                continue;
            }

            float angle = Vector3.Angle(forward, offset);
            if (angle > maxAngle)
            {
                continue;
            }

            if (!HasLineOfSight(origin, candidateCenter, candidate, localCharacter))
            {
                continue;
            }

            float score = angle + distance * TargetDistanceScoreWeight;
            if (score >= bestScore)
            {
                continue;
            }

            bestScore = score;
            target = candidate;
        }

        return target != null;
    }

    private static float GetSwitchTargetDistance()
    {
        Interaction interaction = Interaction.instance;
        float distance = interaction != null ? interaction.distance : 0f;
        return distance > 0f ? distance : FallbackSwitchTargetDistance;
    }

    private static float GetSwitchTargetAngle(float maxDistance)
    {
        Interaction interaction = Interaction.instance;
        float configuredAngle = interaction != null ? interaction.maxCharacterInteractAngle : 0f;
        if (configuredAngle > 0f)
        {
            return configuredAngle;
        }

        float area = interaction != null ? interaction.area : 0f;
        if (area > 0f && maxDistance > 0f)
        {
            return Mathf.Clamp(Mathf.Atan2(area, maxDistance) * Mathf.Rad2Deg * 2f, 12f, 45f);
        }

        return FallbackSwitchTargetAngle;
    }

    private static bool HasLineOfSight(Vector3 origin, Vector3 targetPosition, Character target, Character localCharacter)
    {
        Vector3 offset = targetPosition - origin;
        float distance = offset.magnitude;
        if (distance <= Mathf.Epsilon)
        {
            return false;
        }

        int hitCount = Physics.RaycastNonAlloc(
            origin,
            offset / distance,
            LineOfSightHits,
            distance,
            ~0,
            QueryTriggerInteraction.Ignore);

        for (int i = 0; i < hitCount; i++)
        {
            Collider collider = LineOfSightHits[i].collider;
            if (collider == null)
            {
                continue;
            }

            Character hitCharacter = collider.GetComponentInParent<Character>();
            if (hitCharacter == target || hitCharacter == localCharacter)
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static Vector3 GetCharacterCenter(Character character)
    {
        try
        {
            return character.Center;
        }
        catch
        {
            return character.transform.position;
        }
    }

    private static void SwitchControl(Character target)
    {
        if (!CanShowSwitchPrompt(target))
        {
            return;
        }

        if (!CaptureOriginalLocalCharacterIfAvailable())
        {
            Plugin.Log.LogWarning("Unable to switch dummy control because the original local character could not be found.");
            return;
        }

        if (target == originalLocalCharacter)
        {
            RestoreOriginalLocalControlIfNeeded();
            return;
        }

        AssignLocalControl(target);
        controlledCharacter = target;
        Plugin.Log.LogInfo($"Switched local control to {target.characterName}.");
    }

    private static void RestoreOriginalLocalControlIfNeeded()
    {
        if (!IsControllingAlternate())
        {
            controlledCharacter = null;
            return;
        }

        if (!TryFindOriginalLocalCharacter(out Character originalLocal))
        {
            Plugin.Log.LogWarning("Unable to restore local control because the original local character could not be found.");
            DummyControlPhotonViewAuthority.RestoreControlledView();
            controlledCharacter = null;
            return;
        }

        AssignLocalControl(originalLocal);
        controlledCharacter = null;
        Plugin.Log.LogInfo("Restored local control to the original local character.");
    }

    private static void AssignLocalControl(Character target)
    {
        Character previous = Character.localCharacter;
        ResetInput(previous);
        ResetInput(target);

        Character.localCharacter = target;
        PlayerHandler playerHandler = PlayerHandler.Instance;
        if (playerHandler != null && PhotonNetwork.LocalPlayer != null)
        {
            playerHandler.m_playerCharacterLookup[PhotonNetwork.LocalPlayer.ActorNumber] = target;
        }

        DummyControlPhotonViewAuthority.AssignWriteControl(target, originalLocalCharacter);
        SetSpecCharacterMethod?.Invoke(null, [null]);
        AssignLocalVoiceRecorder(previous, target);
        if (!target.gameObject.activeSelf)
        {
            target.gameObject.SetActive(true);
        }
    }

    private static void AssignLocalVoiceRecorder(Character? previous, Character target)
    {
        Recorder? targetRecorder = target.GetComponentInChildren<Recorder>(true);
        Recorder? previousRecorder = previous != null
            ? previous.GetComponentInChildren<Recorder>(true)
            : null;
        if (previousRecorder != null && previousRecorder != targetRecorder)
        {
            previousRecorder.TransmitEnabled = false;
        }

        if (targetRecorder == null)
        {
            Plugin.Log.LogWarning($"Unable to switch voice recorder because {target.characterName} has no Recorder component.");
            return;
        }

        VoiceClientHandler.LocalPlayerAssigned(targetRecorder);
    }

    private static void ResetInput(Character? character)
    {
        if (character == null || character.input == null || ResetInputMethod == null)
        {
            return;
        }

        ResetInputMethod.Invoke(character.input, []);
    }

    private static bool CaptureOriginalLocalCharacterIfAvailable()
    {
        Character localCharacter = Character.localCharacter;
        if (!IsControllingAlternate() && IsOriginalLocalCharacter(localCharacter))
        {
            originalLocalCharacter = localCharacter;
            return true;
        }

        if (IsOriginalLocalCharacter(originalLocalCharacter))
        {
            return true;
        }

        if (TryFindOriginalLocalCharacter(out Character foundOriginalLocal))
        {
            originalLocalCharacter = foundOriginalLocal;
            return true;
        }

        return false;
    }

    private static bool TryFindOriginalLocalCharacter(out Character character)
    {
        character = null!;
        foreach (Character candidate in Character.AllCharacters)
        {
            if (IsOriginalLocalCharacter(candidate))
            {
                character = candidate;
                originalLocalCharacter = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsOriginalLocalCharacter(Character? character)
    {
        if (PhotonNetwork.LocalPlayer == null)
        {
            return character != null
                && character.photonView != null
                && character.photonView.IsMine
                && !DummyPlayerSpawner.IsDummyPlayer(character);
        }

        return character != null
            && character.photonView != null
            && character.photonView.OwnerActorNr == PhotonNetwork.LocalPlayer.ActorNumber
            && !DummyPlayerSpawner.IsDummyPlayer(character);
    }

    private static bool CanControlCharacter(Character? character)
    {
        return CanUseDummyControl()
            && character != null;
    }

    private static bool IsControllingAlternate()
    {
        return controlledCharacter != null
            && Character.localCharacter == controlledCharacter;
    }

    private static bool CanUseDummyControl()
    {
        return PeakDummyToolsConfig.EnableDummyTools.Value
            && PhotonNetwork.InRoom;
    }

    private static string GetShortcutText(KeyboardShortcut shortcut)
    {
        List<string> keys = [];
        foreach (KeyCode modifier in shortcut.Modifiers)
        {
            string modifierText = FormatKey(modifier);
            if (!string.IsNullOrEmpty(modifierText))
            {
                keys.Add(modifierText);
            }
        }

        string mainKeyText = FormatKey(shortcut.MainKey);
        if (!string.IsNullOrEmpty(mainKeyText))
        {
            keys.Add(mainKeyText);
        }

        return string.Join(" + ", keys);
    }

    private static string FormatKey(KeyCode key)
    {
        return key switch
        {
            KeyCode.LeftAlt => "Alt",
            KeyCode.RightAlt => "Alt",
            KeyCode.LeftControl => "Ctrl",
            KeyCode.RightControl => "Ctrl",
            KeyCode.LeftShift => "Shift",
            KeyCode.RightShift => "Shift",
            KeyCode.LeftCommand => "Cmd",
            KeyCode.RightCommand => "Cmd",
            KeyCode.Alpha0 => "0",
            KeyCode.Alpha1 => "1",
            KeyCode.Alpha2 => "2",
            KeyCode.Alpha3 => "3",
            KeyCode.Alpha4 => "4",
            KeyCode.Alpha5 => "5",
            KeyCode.Alpha6 => "6",
            KeyCode.Alpha7 => "7",
            KeyCode.Alpha8 => "8",
            KeyCode.Alpha9 => "9",
            KeyCode.None => string.Empty,
            _ => key.ToString(),
        };
    }
}
