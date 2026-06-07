using System.Reflection;
using HarmonyLib;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlInteractionStateDriver
{
    private static readonly FieldInfo? CurrentHoveredField = AccessTools.Field(typeof(Interaction), nameof(Interaction.currentHovered));
    private static readonly FieldInfo? BestInteractableField = AccessTools.Field(typeof(Interaction), "bestInteractable");
    private static readonly FieldInfo? BestCharacterField = AccessTools.Field(typeof(Interaction), "bestCharacter");
    private static readonly FieldInfo? CurrentHeldInteractibleField = AccessTools.Field(typeof(Interaction), "currentHeldInteractible");
    private static readonly FieldInfo? CurrentInteractableHeldTimeField = AccessTools.Field(typeof(Interaction), "_cihf");
    private static readonly FieldInfo? ReadyToInteractField = AccessTools.Field(typeof(Interaction), "readyToInteract");
    private static readonly FieldInfo? ReadyToReleaseInteractField = AccessTools.Field(typeof(Interaction), "readyToReleaseInteract");
    private static readonly FieldInfo? BestInteractableNameField = AccessTools.Field(typeof(Interaction), "bestInteractableName");
    private static readonly MethodInfo? CancelHeldInteractMethod = AccessTools.Method(typeof(Interaction), "CancelHeldInteract");
    private static readonly MethodInfo? RefreshInteractablePromptMethod =
        AccessTools.Method(typeof(GUIManager), "RefreshInteractablePrompt");

    internal static void ResetForControlSwitch()
    {
        ResetLocalInteractionState();
    }

    internal static void ResetForDummyRemoval()
    {
        ResetLocalInteractionState();
    }

    private static void ResetLocalInteractionState()
    {
        Interaction interaction = Interaction.instance;
        if (interaction == null)
        {
            return;
        }

        CancelHeldInteractMethod?.Invoke(interaction, []);
        CurrentHoveredField?.SetValue(interaction, null);
        BestInteractableField?.SetValue(interaction, null);
        BestCharacterField?.SetValue(interaction, null);
        CurrentHeldInteractibleField?.SetValue(interaction, null);
        CurrentInteractableHeldTimeField?.SetValue(interaction, 0f);
        ReadyToInteractField?.SetValue(interaction, true);
        ReadyToReleaseInteractField?.SetValue(interaction, false);
        BestInteractableNameField?.SetValue(interaction, "null");

        GUIManager guiManager = GUIManager.instance;
        if (guiManager != null)
        {
            guiManager.currentInteractable = null;
            RefreshInteractablePromptMethod?.Invoke(guiManager, []);
        }
    }
}
