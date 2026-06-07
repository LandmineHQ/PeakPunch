using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlLookSyncDriver
{
    private const float LookValuesChangedEpsilon = 0.0001f;
    private const float PositionChangedEpsilon = 0.02f;
    private const float LocalInputChangedEpsilon = 0.0001f;
    private const float LargePositionCorrectionDistance = 10f;

    private static readonly MethodInfo? RecalculateLookDirectionsMethod = AccessTools.Method(
        typeof(Character),
        "RecalculateLookDirections");

    private static readonly Dictionary<int, Vector2> LastRemoteLookValuesByViewId = [];
    private static readonly Dictionary<int, Vector3> LastRemoteHipLocationsByViewId = [];

    internal static bool IsLocalInputActiveBeforeRemoteSync(CharacterSyncer syncer)
    {
        Character? character = GetControlledSyncCharacter(syncer);
        return character != null && IsLocalInputActive(character.input);
    }

    internal static void HandleRemoteSync(CharacterSyncer syncer, CharacterSyncData data, bool ignoreRemoteSync)
    {
        Character? character = GetControlledSyncCharacter(syncer);
        if (character == null)
        {
            return;
        }

        if (ignoreRemoteSync)
        {
            return;
        }

        ApplyRemotePositionIfChanged(character, data);
        ApplyRemoteLookIfChanged(character, data);
    }

    private static Character? GetControlledSyncCharacter(CharacterSyncer syncer)
    {
        Character? character = syncer != null
            ? syncer.GetComponent<Character>()
            : null;
        if (character == null
            || character.data == null
            || character.photonView == null
            || !DummyControlSwitcher.IsControllingTarget(character))
        {
            return null;
        }

        return character;
    }

    internal static void HandleCharacterRemoved(Character character)
    {
        ResetRemoteState(character);
    }

    internal static void ResetRemoteState(Character? character)
    {
        if (character?.photonView == null)
        {
            return;
        }

        int viewId = character.photonView.ViewID;
        LastRemoteLookValuesByViewId.Remove(viewId);
        LastRemoteHipLocationsByViewId.Remove(viewId);
    }

    private static void ApplyRemoteLookIfChanged(Character character, CharacterSyncData data)
    {
        Vector2 remoteLookValues = new(data.lookValues.x, data.lookValues.y);
        int viewId = character.photonView.ViewID;
        if (LastRemoteLookValuesByViewId.TryGetValue(viewId, out Vector2 lastRemoteLookValues)
            && !HasLookValuesChanged(lastRemoteLookValues, remoteLookValues))
        {
            return;
        }

        LastRemoteLookValuesByViewId[viewId] = remoteLookValues;
        character.data.lookValues = remoteLookValues;
        RecalculateLookDirections(character);
    }

    private static void ApplyRemotePositionIfChanged(Character character, CharacterSyncData data)
    {
        if (character.refs == null || character.refs.ragdoll == null)
        {
            return;
        }

        Bodypart hip = character.GetBodypart(BodypartType.Hip);
        if (hip == null || hip.Rig == null)
        {
            return;
        }

        Vector3 remoteHipLocation = new(data.hipLocation.x, data.hipLocation.y, data.hipLocation.z);
        int viewId = character.photonView.ViewID;
        if (LastRemoteHipLocationsByViewId.TryGetValue(viewId, out Vector3 lastRemoteHipLocation)
            && !HasPositionChanged(lastRemoteHipLocation, remoteHipLocation))
        {
            return;
        }

        LastRemoteHipLocationsByViewId[viewId] = remoteHipLocation;
        Vector3 delta = remoteHipLocation - hip.Rig.position;
        if (delta.sqrMagnitude <= Mathf.Epsilon)
        {
            return;
        }

        character.refs.ragdoll.MoveAllRigsInDirection(delta);
        if (delta.sqrMagnitude > LargePositionCorrectionDistance * LargePositionCorrectionDistance)
        {
            character.refs.ragdoll.HaltBodyVelocity();
        }
    }

    private static bool HasLookValuesChanged(Vector2 left, Vector2 right)
    {
        return (left - right).sqrMagnitude > LookValuesChangedEpsilon * LookValuesChangedEpsilon;
    }

    private static bool HasPositionChanged(Vector3 left, Vector3 right)
    {
        return (left - right).sqrMagnitude > PositionChangedEpsilon * PositionChangedEpsilon;
    }

    private static bool IsLocalInputActive(CharacterInput input)
    {
        if (input == null)
        {
            return false;
        }

        return input.lookInput.sqrMagnitude > LocalInputChangedEpsilon * LocalInputChangedEpsilon
            || input.movementInput.sqrMagnitude > LocalInputChangedEpsilon * LocalInputChangedEpsilon
            || input.sprintIsPressed
            || input.sprintWasPressed
            || input.sprintToggleWasPressed
            || input.jumpIsPressed
            || input.jumpWasPressed
            || input.crouchIsPressed
            || input.crouchWasPressed
            || input.crouchToggleWasPressed
            || input.usePrimaryIsPressed
            || input.usePrimaryWasPressed
            || input.usePrimaryWasReleased
            || input.useSecondaryIsPressed
            || input.useSecondaryWasPressed
            || input.useSecondaryWasReleased
            || input.interactIsPressed
            || input.interactWasPressed
            || input.interactWasReleased
            || input.dropIsPressed
            || input.dropWasPressed
            || input.dropWasReleased
            || input.emoteIsPressed;
    }

    private static void RecalculateLookDirections(Character character)
    {
        if (RecalculateLookDirectionsMethod == null)
        {
            Plugin.Log.LogWarning("Unable to recalculate controlled look directions because Character.RecalculateLookDirections was not found.");
            return;
        }

        RecalculateLookDirectionsMethod.Invoke(character, []);
    }
}
