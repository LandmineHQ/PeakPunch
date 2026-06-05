using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlLookSyncDriver
{
    private const float LookValuesChangedEpsilon = 0.0001f;

    private static readonly MethodInfo? RecalculateLookDirectionsMethod = AccessTools.Method(
        typeof(Character),
        "RecalculateLookDirections");

    private static readonly Dictionary<int, Vector2> LastRemoteLookValuesByViewId = [];

    internal static void HandleRemoteSync(CharacterSyncer syncer, CharacterSyncData data)
    {
        Character character = syncer != null
            ? syncer.GetComponent<Character>()
            : null!;
        if (character == null
            || character.data == null
            || character.photonView == null
            || !DummyControlSwitcher.IsControllingTarget(character))
        {
            return;
        }

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

    internal static void HandleCharacterRemoved(Character character)
    {
        ResetRemoteLook(character);
    }

    internal static void ResetRemoteLook(Character? character)
    {
        if (character?.photonView == null)
        {
            return;
        }

        LastRemoteLookValuesByViewId.Remove(character.photonView.ViewID);
    }

    private static bool HasLookValuesChanged(Vector2 left, Vector2 right)
    {
        return (left - right).sqrMagnitude > LookValuesChangedEpsilon * LookValuesChangedEpsilon;
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
