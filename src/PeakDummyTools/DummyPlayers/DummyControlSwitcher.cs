using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using PeakDummyTools.Configuration;
using PeakDummyTools.Localization;
using Photon.Pun;
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

    private static Character? hostCharacter;
    private static Character? controlledDummy;

    internal static void Update()
    {
        if (!CanUseDummyControl())
        {
            RestoreHostControlIfNeeded();
            return;
        }

        if (controlledDummy != null && !CanControlCharacter(controlledDummy))
        {
            RestoreHostControlIfNeeded();
        }

        CaptureHostCharacterIfAvailable();

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

        if (controlledDummy == null || !CanControlCharacter(controlledDummy))
        {
            return false;
        }

        character = controlledDummy;
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

    internal static bool CanShowSwitchPrompt(Character character)
    {
        if (!CanUseDummyControl() || character == null || character == Character.localCharacter)
        {
            return false;
        }

        CaptureHostCharacterIfAvailable();

        if (DummyPlayerSpawner.IsDummyPlayer(character))
        {
            return CanControlCharacter(character);
        }

        return IsControllingDummy() && hostCharacter != null && character == hostCharacter;
    }

    internal static void HandleCharacterRemoved(Character character)
    {
        if (character == null)
        {
            return;
        }

        if (character == hostCharacter)
        {
            hostCharacter = null;
        }

        if (character != controlledDummy)
        {
            return;
        }

        controlledDummy = null;
        if (TryFindHostCharacter(out Character host))
        {
            AssignLocalControl(host);
            Plugin.Log.LogInfo("Restored host control because the controlled dummy was removed.");
        }
    }

    private static bool TryGetSwitchPrompt(Character character, out string keyText, out string text)
    {
        keyText = string.Empty;
        text = string.Empty;
        if (!CanShowSwitchPrompt(character))
        {
            return false;
        }

        PeakDummyToolsTextKey textKey = DummyPlayerSpawner.IsDummyPlayer(character)
            ? PeakDummyToolsTextKey.SwitchControlToDummy
            : PeakDummyToolsTextKey.SwitchControlToHost;
        keyText = GetShortcutText();
        text = PeakDummyToolsLocalization.Get(textKey);
        return true;
    }

    private static void SwitchFromShortcut()
    {
        if (TryGetSwitchTarget(out Character target))
        {
            SwitchControl(target);
            return;
        }

        if (IsControllingDummy())
        {
            RestoreHostControlIfNeeded();
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

    private static bool TryFindLookTarget(out Character target)
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
            if (!CanShowSwitchPrompt(candidate))
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

        if (!CaptureHostCharacterIfAvailable())
        {
            Plugin.Log.LogWarning("Unable to switch dummy control because the host character could not be found.");
            return;
        }

        if (target == hostCharacter)
        {
            RestoreHostControlIfNeeded();
            return;
        }

        AssignLocalControl(target);
        controlledDummy = target;
        Plugin.Log.LogInfo($"Switched local control to {target.characterName}.");
    }

    private static void RestoreHostControlIfNeeded()
    {
        if (!IsControllingDummy())
        {
            controlledDummy = null;
            return;
        }

        if (!TryFindHostCharacter(out Character host))
        {
            Plugin.Log.LogWarning("Unable to restore host control because the host character could not be found.");
            controlledDummy = null;
            return;
        }

        AssignLocalControl(host);
        controlledDummy = null;
        Plugin.Log.LogInfo("Restored local control to the host character.");
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

        SetSpecCharacterMethod?.Invoke(null, [null]);
        if (!target.gameObject.activeSelf)
        {
            target.gameObject.SetActive(true);
        }
    }

    private static void ResetInput(Character? character)
    {
        if (character == null || character.input == null || ResetInputMethod == null)
        {
            return;
        }

        ResetInputMethod.Invoke(character.input, []);
    }

    private static bool CaptureHostCharacterIfAvailable()
    {
        Character localCharacter = Character.localCharacter;
        if (IsHostCharacter(localCharacter))
        {
            hostCharacter = localCharacter;
            return true;
        }

        if (IsHostCharacter(hostCharacter))
        {
            return true;
        }

        if (TryFindHostCharacter(out Character foundHost))
        {
            hostCharacter = foundHost;
            return true;
        }

        return false;
    }

    private static bool TryFindHostCharacter(out Character character)
    {
        character = null!;
        foreach (Character candidate in Character.AllCharacters)
        {
            if (IsHostCharacter(candidate))
            {
                character = candidate;
                hostCharacter = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool IsHostCharacter(Character? character)
    {
        return character != null
            && character.photonView != null
            && character.photonView.IsMine
            && !DummyPlayerSpawner.IsDummyPlayer(character);
    }

    private static bool CanControlCharacter(Character? character)
    {
        return character != null
            && character.photonView != null
            && character.photonView.IsMine
            && DummyPlayerSpawner.IsDummyPlayer(character)
            && !character.data.dead
            && !character.data.fullyPassedOut;
    }

    private static bool IsControllingDummy()
    {
        return controlledDummy != null
            && Character.localCharacter == controlledDummy
            && DummyPlayerSpawner.IsDummyPlayer(controlledDummy);
    }

    private static bool CanUseDummyControl()
    {
        return PeakDummyToolsConfig.EnableDummyTools.Value
            && PhotonNetwork.InRoom
            && PhotonNetwork.IsMasterClient;
    }

    private static string GetShortcutText()
    {
        List<string> keys = [];
        foreach (KeyCode modifier in PeakDummyToolsConfig.SwitchControlShortcut.Value.Modifiers)
        {
            string modifierText = FormatKey(modifier);
            if (!string.IsNullOrEmpty(modifierText))
            {
                keys.Add(modifierText);
            }
        }

        string mainKeyText = FormatKey(PeakDummyToolsConfig.SwitchControlShortcut.Value.MainKey);
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
