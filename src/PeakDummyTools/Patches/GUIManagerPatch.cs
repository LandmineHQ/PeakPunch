using HarmonyLib;
using PeakDummyTools.DummyPlayers;
using System.Reflection;

namespace PeakDummyTools.Patches;

[HarmonyPatch(typeof(GUIManager))]
internal static class GUIManagerPatch
{
    private static readonly MethodInfo RefreshInteractablePromptMethod =
        AccessTools.Method(typeof(GUIManager), "RefreshInteractablePrompt");

    private static GUIManager? lastGuiManager;
    private static PromptState lastPromptState;

    [HarmonyPatch("RefreshInteractablePrompt")]
    [HarmonyPostfix]
    private static void RefreshInteractablePromptPostfix(GUIManager __instance)
    {
        DummySwitchPromptUi.RefreshAfterNativePrompt(__instance);
        UpdateLastPromptState(__instance);
    }

    [HarmonyPatch("LateUpdate")]
    [HarmonyPostfix]
    private static void LateUpdatePostfix(GUIManager __instance)
    {
        if (!TryGetPromptState(out PromptState promptState))
        {
            ResetPromptState(__instance);
            return;
        }

        if (lastGuiManager == __instance && promptState.Equals(lastPromptState))
        {
            return;
        }

        RefreshInteractablePromptMethod?.Invoke(__instance, []);
        UpdateLastPromptState(__instance);
    }

    private static void UpdateLastPromptState(GUIManager guiManager)
    {
        lastGuiManager = guiManager;
        if (TryGetPromptState(out PromptState promptState))
        {
            lastPromptState = promptState;
            return;
        }

        lastPromptState = default;
    }

    private static void ResetPromptState(GUIManager guiManager)
    {
        lastGuiManager = guiManager;
        lastPromptState = default;
    }

    private static bool TryGetPromptState(out PromptState promptState)
    {
        promptState = default;
        Character localCharacter = Character.localCharacter;
        Interaction interaction = Interaction.instance;
        if (localCharacter == null || interaction?.currentHovered == null)
        {
            return false;
        }

        IInteractible interactible = interaction.currentHovered;
        bool primaryInteractible = true;
        bool secondaryInteractible = false;
        string primaryText = interactible.GetInteractionText() ?? string.Empty;
        string secondaryText = string.Empty;

        if (interactible is CharacterInteractible characterInteractible)
        {
            primaryInteractible = characterInteractible.IsPrimaryInteractible(localCharacter);
            secondaryInteractible = characterInteractible.IsSecondaryInteractible(localCharacter);
            if (secondaryInteractible)
            {
                secondaryText = characterInteractible.GetSecondaryInteractionText() ?? string.Empty;
            }
        }

        promptState = new PromptState(
            interactible,
            primaryInteractible,
            secondaryInteractible,
            primaryText,
            secondaryText);
        return true;
    }

    private readonly struct PromptState
    {
        private readonly IInteractible? interactible;
        private readonly bool primaryInteractible;
        private readonly bool secondaryInteractible;
        private readonly string primaryText;
        private readonly string secondaryText;

        internal PromptState(
            IInteractible interactible,
            bool primaryInteractible,
            bool secondaryInteractible,
            string primaryText,
            string secondaryText)
        {
            this.interactible = interactible;
            this.primaryInteractible = primaryInteractible;
            this.secondaryInteractible = secondaryInteractible;
            this.primaryText = primaryText;
            this.secondaryText = secondaryText;
        }

        public bool Equals(PromptState other)
        {
            return ReferenceEquals(interactible, other.interactible)
                && primaryInteractible == other.primaryInteractible
                && secondaryInteractible == other.secondaryInteractible
                && primaryText == other.primaryText
                && secondaryText == other.secondaryText;
        }
    }
}
