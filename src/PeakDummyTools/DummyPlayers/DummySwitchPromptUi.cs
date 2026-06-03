using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakDummyTools.DummyPlayers;

internal static class DummySwitchPromptUi
{
    private const float KeyMinWidth = 72f;
    private const float KeyPreferredWidth = 92f;
    private const float PromptSpacing = 8f;
    private const float ManualVerticalGap = 4f;

    private static SwitchPromptView? promptView;
    private static GUIManager? sourceGuiManager;

    internal static void RefreshAfterNativePrompt(GUIManager guiManager)
    {
        UpdatePrompt(guiManager);
    }

    private static void UpdatePrompt(GUIManager guiManager)
    {
        if (!DummyControlSwitcher.TryGetCurrentHoveredSwitchPrompt(out string keyText, out string labelText))
        {
            promptView?.Hide();
            return;
        }

        if (guiManager == null)
        {
            promptView?.Hide();
            return;
        }

        SwitchPromptView? view = EnsurePromptView(guiManager);
        if (view == null)
        {
            return;
        }

        view.Show(keyText, labelText);
    }

    private static SwitchPromptView? EnsurePromptView(GUIManager guiManager)
    {
        if (promptView is { IsValid: true } && sourceGuiManager == guiManager)
        {
            return promptView;
        }

        promptView?.Destroy();
        promptView = null;
        sourceGuiManager = null;

        if (!TryGetTemplate(guiManager, out GameObject template, out TMP_Text sourceLabel))
        {
            return null;
        }

        try
        {
            GameObject promptRow = Object.Instantiate(template, template.transform.parent);
            promptRow.name = "PeakDummyTools Switch Control Prompt";
            promptRow.SetActive(false);

            SwitchPromptView? view = ConfigureClonedPromptRow(guiManager, promptRow, template, sourceLabel);
            if (view == null)
            {
                Object.Destroy(promptRow);
                return null;
            }

            sourceGuiManager = guiManager;
            promptView = view;
            return view;
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"Failed to clone PEAK interaction prompt UI: {ex.Message}");
            return null;
        }
    }

    private static bool TryGetTemplate(GUIManager guiManager, out GameObject template, out TMP_Text sourceLabel)
    {
        template = null!;
        sourceLabel = null!;

        if (guiManager.interactPromptSecondary != null && guiManager.secondaryInteractPromptText != null)
        {
            template = guiManager.interactPromptSecondary;
            sourceLabel = guiManager.secondaryInteractPromptText;
            return true;
        }

        if (guiManager.interactPromptPrimary != null && guiManager.interactPromptText != null)
        {
            template = guiManager.interactPromptPrimary;
            sourceLabel = guiManager.interactPromptText;
            return true;
        }

        return false;
    }

    private static SwitchPromptView? ConfigureClonedPromptRow(
        GUIManager guiManager,
        GameObject promptRow,
        GameObject template,
        TMP_Text sourceLabel)
    {
        TMP_Text? keyText = FindInputIconText(promptRow);
        RemoveComponentWriters(promptRow);

        TMP_Text[] textComponents = promptRow.GetComponentsInChildren<TMP_Text>(true);
        TMP_Text? labelText = FindClonedSourceText(promptRow, template, sourceLabel)
            ?? FindLabelText(textComponents, sourceLabel);
        keyText ??= FindKeyText(textComponents, labelText);

        if (labelText == null)
        {
            labelText = CreateText("Label", promptRow.transform, sourceLabel);
            EnsureHorizontalLayout(promptRow);
        }

        if (keyText == null)
        {
            keyText = CreateText("Key", promptRow.transform, labelText);
            keyText.transform.SetSiblingIndex(labelText.transform.GetSiblingIndex());
            EnsureHorizontalLayout(promptRow);
        }

        foreach (TMP_Text textComponent in promptRow.GetComponentsInChildren<TMP_Text>(true))
        {
            if (textComponent != keyText && textComponent != labelText)
            {
                textComponent.text = string.Empty;
            }
        }

        SetActivePath(keyText.transform, promptRow.transform);
        SetActivePath(labelText.transform, promptRow.transform);
        PrepareKeyText(keyText);
        PrepareLabelText(labelText);
        RestoreCanvasGroups(promptRow);

        SwitchPromptView view = new(guiManager, promptRow, keyText, labelText);
        view.PlaceAfterNativePrompts();
        return view;
    }

    private static TMP_Text? FindInputIconText(GameObject promptRow)
    {
        foreach (InputIcon inputIcon in promptRow.GetComponentsInChildren<InputIcon>(true))
        {
            TMP_Text text = inputIcon.GetComponent<TMP_Text>()
                ?? inputIcon.GetComponentInChildren<TMP_Text>(true);
            if (text != null)
            {
                return text;
            }
        }

        return null;
    }

    private static void RemoveComponentWriters(GameObject promptRow)
    {
        foreach (UI_Interaction component in promptRow.GetComponentsInChildren<UI_Interaction>(true))
        {
            DisableAndDestroy(component);
        }

        foreach (InputIcon component in promptRow.GetComponentsInChildren<InputIcon>(true))
        {
            DisableAndDestroy(component);
        }

        foreach (LocalizedText component in promptRow.GetComponentsInChildren<LocalizedText>(true))
        {
            DisableAndDestroy(component);
        }

        foreach (InLineInputPrompts component in promptRow.GetComponentsInChildren<InLineInputPrompts>(true))
        {
            DisableAndDestroy(component);
        }
    }

    private static TMP_Text? FindClonedSourceText(
        GameObject promptRow,
        GameObject template,
        TMP_Text sourceLabel)
    {
        List<int> siblingPath = [];
        Transform? current = sourceLabel.transform;
        while (current != null && current != template.transform)
        {
            siblingPath.Add(current.GetSiblingIndex());
            current = current.parent;
        }

        if (current != template.transform)
        {
            return null;
        }

        Transform clonedTransform = promptRow.transform;
        for (int index = siblingPath.Count - 1; index >= 0; index--)
        {
            int siblingIndex = siblingPath[index];
            if (siblingIndex < 0 || siblingIndex >= clonedTransform.childCount)
            {
                return null;
            }

            clonedTransform = clonedTransform.GetChild(siblingIndex);
        }

        return clonedTransform.GetComponent<TMP_Text>();
    }

    private static TMP_Text? FindLabelText(TMP_Text[] textComponents, TMP_Text sourceLabel)
    {
        foreach (TMP_Text textComponent in textComponents)
        {
            if (textComponent.name == sourceLabel.name)
            {
                return textComponent;
            }
        }

        return textComponents.Length > 0 ? textComponents[textComponents.Length - 1] : null;
    }

    private static TMP_Text? FindKeyText(TMP_Text[] textComponents, TMP_Text? labelText)
    {
        foreach (TMP_Text textComponent in textComponents)
        {
            if (textComponent != labelText)
            {
                return textComponent;
            }
        }

        return null;
    }

    private static TextMeshProUGUI CreateText(string name, Transform parent, TMP_Text template)
    {
        GameObject textObject = new($"PeakDummyTools {name}");
        textObject.transform.SetParent(parent, false);

        TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
        CopyTextStyle(template, text);
        return text;
    }

    private static void CopyTextStyle(TMP_Text source, TMP_Text destination)
    {
        destination.font = source.font;
        destination.fontSharedMaterial = source.fontSharedMaterial;
        destination.spriteAsset = source.spriteAsset;
        destination.color = source.color;
        destination.fontSize = source.fontSize;
        destination.fontStyle = source.fontStyle;
        destination.alignment = source.alignment;
        destination.textWrappingMode = source.textWrappingMode;
        destination.overflowMode = source.overflowMode;
        if (destination is TextMeshProUGUI destinationUi)
        {
            destinationUi.raycastTarget = false;
        }
    }

    private static void PrepareKeyText(TMP_Text keyText)
    {
        keyText.text = string.Empty;
        keyText.alignment = TextAlignmentOptions.Center;
        keyText.textWrappingMode = TextWrappingModes.NoWrap;
        keyText.overflowMode = TextOverflowModes.Overflow;
        keyText.enableAutoSizing = false;

        RectTransform rectTransform = keyText.rectTransform;
        rectTransform.sizeDelta = new Vector2(
            Mathf.Max(rectTransform.sizeDelta.x, KeyPreferredWidth),
            rectTransform.sizeDelta.y);

        LayoutElement layoutElement = keyText.GetComponent<LayoutElement>() ?? keyText.gameObject.AddComponent<LayoutElement>();
        layoutElement.minWidth = KeyMinWidth;
        layoutElement.preferredWidth = KeyPreferredWidth;
        layoutElement.flexibleWidth = 0f;

        if (keyText is TextMeshProUGUI keyTextUi)
        {
            keyTextUi.raycastTarget = false;
        }
    }

    private static void PrepareLabelText(TMP_Text labelText)
    {
        labelText.text = string.Empty;
        labelText.alignment = TextAlignmentOptions.Left;
        labelText.textWrappingMode = TextWrappingModes.NoWrap;
        labelText.overflowMode = TextOverflowModes.Overflow;

        LayoutElement layoutElement = labelText.GetComponent<LayoutElement>() ?? labelText.gameObject.AddComponent<LayoutElement>();
        layoutElement.flexibleWidth = 1f;

        if (labelText is TextMeshProUGUI labelTextUi)
        {
            labelTextUi.raycastTarget = false;
        }
    }

    private static void EnsureHorizontalLayout(GameObject promptRow)
    {
        if (promptRow.GetComponent<HorizontalLayoutGroup>() != null)
        {
            return;
        }

        HorizontalLayoutGroup layoutGroup = promptRow.AddComponent<HorizontalLayoutGroup>();
        layoutGroup.childAlignment = TextAnchor.MiddleLeft;
        layoutGroup.spacing = PromptSpacing;
        layoutGroup.childControlWidth = true;
        layoutGroup.childControlHeight = true;
        layoutGroup.childForceExpandWidth = false;
        layoutGroup.childForceExpandHeight = false;
    }

    private static void SetActivePath(Transform target, Transform stopBefore)
    {
        for (Transform? current = target; current != null && current != stopBefore; current = current.parent)
        {
            if (!current.gameObject.activeSelf)
            {
                current.gameObject.SetActive(true);
            }
        }
    }

    private static void RestoreCanvasGroups(GameObject promptRow)
    {
        foreach (CanvasGroup canvasGroup in promptRow.GetComponentsInChildren<CanvasGroup>(true))
        {
            canvasGroup.alpha = 1f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }
    }

    private static void DisableAndDestroy(Component component)
    {
        if (component is Behaviour behaviour)
        {
            behaviour.enabled = false;
        }

        Object.Destroy(component);
    }

    private sealed class SwitchPromptView
    {
        private readonly GUIManager guiManager;
        private readonly GameObject root;
        private readonly TMP_Text keyText;
        private readonly TMP_Text labelText;
        private readonly RectTransform rootRect;

        internal SwitchPromptView(GUIManager guiManager, GameObject root, TMP_Text keyText, TMP_Text labelText)
        {
            this.guiManager = guiManager;
            this.root = root;
            this.keyText = keyText;
            this.labelText = labelText;
            rootRect = root.GetComponent<RectTransform>();
        }

        internal bool IsValid => guiManager != null
            && root != null
            && keyText != null
            && labelText != null
            && rootRect != null;

        internal void Show(string key, string label)
        {
            if (!IsValid)
            {
                return;
            }

            keyText.text = key;
            labelText.text = label;
            PlaceAfterNativePrompts();
            if (!root.activeSelf)
            {
                root.SetActive(true);
            }
        }

        internal void Hide()
        {
            if (root != null && root.activeSelf)
            {
                root.SetActive(false);
            }
        }

        internal void Destroy()
        {
            if (root != null)
            {
                Object.Destroy(root);
            }
        }

        internal void PlaceAfterNativePrompts()
        {
            if (!IsValid)
            {
                return;
            }

            Transform parent = root.transform.parent;
            GameObject? primary = guiManager.interactPromptPrimary;
            GameObject? secondary = guiManager.interactPromptSecondary;
            GameObject? hold = guiManager.interactPromptHold;
            int siblingIndex = GetLastNativePromptSiblingIndex(parent, primary, secondary, hold);
            if (siblingIndex >= 0)
            {
                root.transform.SetSiblingIndex(siblingIndex + 1);
            }

            if (parent != null && parent.GetComponent<LayoutGroup>() != null)
            {
                return;
            }

            UpdateManualPosition(primary, secondary, hold);
        }

        private static int GetLastNativePromptSiblingIndex(
            Transform parent,
            params GameObject?[] prompts)
        {
            int siblingIndex = -1;
            foreach (GameObject? prompt in prompts)
            {
                if (prompt == null || prompt.transform.parent != parent)
                {
                    continue;
                }

                siblingIndex = Mathf.Max(siblingIndex, prompt.transform.GetSiblingIndex());
            }

            return siblingIndex;
        }

        private void UpdateManualPosition(GameObject? primary, GameObject? secondary, GameObject? hold)
        {
            RectTransform? primaryRect = primary != null ? primary.GetComponent<RectTransform>() : null;
            RectTransform? secondaryRect = secondary != null ? secondary.GetComponent<RectTransform>() : null;
            RectTransform? holdRect = hold != null ? hold.GetComponent<RectTransform>() : null;
            RectTransform? activeBase = GetActiveBasePrompt(primary, secondary, hold);

            if (activeBase == null)
            {
                if (primaryRect != null)
                {
                    CopyRectPlacement(primaryRect, rootRect);
                }

                return;
            }

            CopyRectPlacement(activeBase, rootRect);
            rootRect.anchoredPosition = activeBase.anchoredPosition + GetPromptStep(primaryRect, secondaryRect, holdRect, activeBase);
        }

        private static RectTransform? GetActiveBasePrompt(GameObject? primary, GameObject? secondary, GameObject? hold)
        {
            if (hold != null && hold.activeSelf)
            {
                return hold.GetComponent<RectTransform>();
            }

            if (secondary != null && secondary.activeSelf)
            {
                return secondary.GetComponent<RectTransform>();
            }

            if (primary != null && primary.activeSelf)
            {
                return primary.GetComponent<RectTransform>();
            }

            return null;
        }

        private static Vector2 GetPromptStep(
            RectTransform? primary,
            RectTransform? secondary,
            RectTransform? hold,
            RectTransform activeBase)
        {
            if (primary != null && secondary != null && primary.parent == secondary.parent)
            {
                Vector2 secondaryStep = secondary.anchoredPosition - primary.anchoredPosition;
                if (secondaryStep.sqrMagnitude > 1f)
                {
                    return secondaryStep;
                }
            }

            if (secondary != null && hold != null && secondary.parent == hold.parent)
            {
                Vector2 holdStep = hold.anchoredPosition - secondary.anchoredPosition;
                if (holdStep.sqrMagnitude > 1f)
                {
                    return holdStep;
                }
            }

            return new Vector2(0f, -activeBase.rect.height - ManualVerticalGap);
        }

        private static void CopyRectPlacement(RectTransform source, RectTransform destination)
        {
            destination.anchorMin = source.anchorMin;
            destination.anchorMax = source.anchorMax;
            destination.pivot = source.pivot;
            destination.sizeDelta = source.sizeDelta;
            destination.anchoredPosition = source.anchoredPosition;
            destination.localScale = source.localScale;
            destination.localRotation = source.localRotation;
        }
    }
}
