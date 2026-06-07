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

    internal static LocalInputSnapshot? CaptureLocalInputBeforeRemoteSync(CharacterSyncer syncer)
    {
        Character? character = GetControlledSyncCharacter(syncer);
        return character != null && IsLocalInputActive(character.input)
            ? LocalInputSnapshot.Capture(character.input)
            : null;
    }

    internal static void HandleRemoteSync(
        CharacterSyncer syncer,
        CharacterSyncData data,
        LocalInputSnapshot? localInputSnapshot)
    {
        Character? character = GetControlledSyncCharacter(syncer);
        if (character == null)
        {
            return;
        }

        if (localInputSnapshot != null)
        {
            localInputSnapshot.Restore(character.input);
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

    internal sealed class LocalInputSnapshot
    {
        private readonly Vector2 movementInput;
        private readonly Vector2 lookInput;
        private readonly float scrollInput;
        private readonly bool crouchIsPressed;
        private readonly bool crouchWasPressed;
        private readonly bool crouchToggleWasPressed;
        private readonly bool sprintIsPressed;
        private readonly bool sprintToggleIsPressed;
        private readonly bool sprintWasPressed;
        private readonly bool sprintToggleWasPressed;
        private readonly bool pauseWasPressed;
        private readonly bool jumpWasPressed;
        private readonly bool jumpIsPressed;
        private readonly bool interactWasPressed;
        private readonly bool interactIsPressed;
        private readonly bool interactWasReleased;
        private readonly bool dropWasPressed;
        private readonly bool dropIsPressed;
        private readonly bool dropWasReleased;
        private readonly bool usePrimaryWasPressed;
        private readonly bool usePrimaryIsPressed;
        private readonly bool usePrimaryWasReleased;
        private readonly bool useSecondaryWasPressed;
        private readonly bool useSecondaryIsPressed;
        private readonly bool useSecondaryWasReleased;
        private readonly bool pingWasPressed;
        private readonly bool selectSlotForwardWasPressed;
        private readonly bool selectSlotBackwardWasPressed;
        private readonly bool unselectSlotWasPressed;
        private readonly bool selectBackpackWasPressed;
        private readonly bool scrollBackwardWasPressed;
        private readonly bool scrollForwardWasPressed;
        private readonly bool scrollBackwardIsPressed;
        private readonly bool scrollForwardIsPressed;
        private readonly bool emoteIsPressed;
        private readonly bool spectateLeftWasPressed;
        private readonly bool spectateRightWasPressed;
        private readonly bool pushToTalkPressed;
        private readonly bool itemSwitchBlocked;

        private LocalInputSnapshot(CharacterInput input)
        {
            movementInput = input.movementInput;
            lookInput = input.lookInput;
            scrollInput = input.scrollInput;
            crouchIsPressed = input.crouchIsPressed;
            crouchWasPressed = input.crouchWasPressed;
            crouchToggleWasPressed = input.crouchToggleWasPressed;
            sprintIsPressed = input.sprintIsPressed;
            sprintToggleIsPressed = input.sprintToggleIsPressed;
            sprintWasPressed = input.sprintWasPressed;
            sprintToggleWasPressed = input.sprintToggleWasPressed;
            pauseWasPressed = input.pauseWasPressed;
            jumpWasPressed = input.jumpWasPressed;
            jumpIsPressed = input.jumpIsPressed;
            interactWasPressed = input.interactWasPressed;
            interactIsPressed = input.interactIsPressed;
            interactWasReleased = input.interactWasReleased;
            dropWasPressed = input.dropWasPressed;
            dropIsPressed = input.dropIsPressed;
            dropWasReleased = input.dropWasReleased;
            usePrimaryWasPressed = input.usePrimaryWasPressed;
            usePrimaryIsPressed = input.usePrimaryIsPressed;
            usePrimaryWasReleased = input.usePrimaryWasReleased;
            useSecondaryWasPressed = input.useSecondaryWasPressed;
            useSecondaryIsPressed = input.useSecondaryIsPressed;
            useSecondaryWasReleased = input.useSecondaryWasReleased;
            pingWasPressed = input.pingWasPressed;
            selectSlotForwardWasPressed = input.selectSlotForwardWasPressed;
            selectSlotBackwardWasPressed = input.selectSlotBackwardWasPressed;
            unselectSlotWasPressed = input.unselectSlotWasPressed;
            selectBackpackWasPressed = input.selectBackpackWasPressed;
            scrollBackwardWasPressed = input.scrollBackwardWasPressed;
            scrollForwardWasPressed = input.scrollForwardWasPressed;
            scrollBackwardIsPressed = input.scrollBackwardIsPressed;
            scrollForwardIsPressed = input.scrollForwardIsPressed;
            emoteIsPressed = input.emoteIsPressed;
            spectateLeftWasPressed = input.spectateLeftWasPressed;
            spectateRightWasPressed = input.spectateRightWasPressed;
            pushToTalkPressed = input.pushToTalkPressed;
            itemSwitchBlocked = input.itemSwitchBlocked;
        }

        internal static LocalInputSnapshot Capture(CharacterInput input)
        {
            return new LocalInputSnapshot(input);
        }

        internal void Restore(CharacterInput input)
        {
            if (input == null)
            {
                return;
            }

            input.movementInput = movementInput;
            input.lookInput = lookInput;
            input.scrollInput = scrollInput;
            input.crouchIsPressed = crouchIsPressed;
            input.crouchWasPressed = crouchWasPressed;
            input.crouchToggleWasPressed = crouchToggleWasPressed;
            input.sprintIsPressed = sprintIsPressed;
            input.sprintToggleIsPressed = sprintToggleIsPressed;
            input.sprintWasPressed = sprintWasPressed;
            input.sprintToggleWasPressed = sprintToggleWasPressed;
            input.pauseWasPressed = pauseWasPressed;
            input.jumpWasPressed = jumpWasPressed;
            input.jumpIsPressed = jumpIsPressed;
            input.interactWasPressed = interactWasPressed;
            input.interactIsPressed = interactIsPressed;
            input.interactWasReleased = interactWasReleased;
            input.dropWasPressed = dropWasPressed;
            input.dropIsPressed = dropIsPressed;
            input.dropWasReleased = dropWasReleased;
            input.usePrimaryWasPressed = usePrimaryWasPressed;
            input.usePrimaryIsPressed = usePrimaryIsPressed;
            input.usePrimaryWasReleased = usePrimaryWasReleased;
            input.useSecondaryWasPressed = useSecondaryWasPressed;
            input.useSecondaryIsPressed = useSecondaryIsPressed;
            input.useSecondaryWasReleased = useSecondaryWasReleased;
            input.pingWasPressed = pingWasPressed;
            input.selectSlotForwardWasPressed = selectSlotForwardWasPressed;
            input.selectSlotBackwardWasPressed = selectSlotBackwardWasPressed;
            input.unselectSlotWasPressed = unselectSlotWasPressed;
            input.selectBackpackWasPressed = selectBackpackWasPressed;
            input.scrollBackwardWasPressed = scrollBackwardWasPressed;
            input.scrollForwardWasPressed = scrollForwardWasPressed;
            input.scrollBackwardIsPressed = scrollBackwardIsPressed;
            input.scrollForwardIsPressed = scrollForwardIsPressed;
            input.emoteIsPressed = emoteIsPressed;
            input.spectateLeftWasPressed = spectateLeftWasPressed;
            input.spectateRightWasPressed = spectateRightWasPressed;
            input.pushToTalkPressed = pushToTalkPressed;
            input.itemSwitchBlocked = itemSwitchBlocked;
        }
    }
}
