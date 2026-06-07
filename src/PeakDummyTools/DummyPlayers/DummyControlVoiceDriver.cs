using System.Collections.Generic;
using Photon.Voice.Unity;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlVoiceDriver
{
    private static readonly Dictionary<Recorder, VoiceRecorderState> SavedVoiceRecorderStates = [];

    private sealed class VoiceRecorderState
    {
        internal VoiceRecorderState(bool transmitEnabled, bool recordingEnabled)
        {
            TransmitEnabled = transmitEnabled;
            RecordingEnabled = recordingEnabled;
        }

        internal bool TransmitEnabled { get; }

        internal bool RecordingEnabled { get; }
    }

    internal static void AssignLocalVoiceRecorder(Character? previous, Character target)
    {
        Recorder? targetRecorder = target.GetComponentInChildren<Recorder>(true);
        Recorder? previousRecorder = previous != null
            ? previous.GetComponentInChildren<Recorder>(true)
            : null;

        if (targetRecorder == null)
        {
            Plugin.Log.LogWarning($"Unable to switch voice recorder because {target.characterName} has no Recorder component.");
        }
        else
        {
            EnableProximityVoiceTriggers(target);
            VoiceClientHandler.LocalPlayerAssigned(targetRecorder);
            RestoreSavedVoiceRecorderState(targetRecorder);
            targetRecorder.RecordingEnabled = true;
        }

        if (previousRecorder != null && previousRecorder != targetRecorder)
        {
            SaveVoiceRecorderState(previousRecorder);
            ForceRecorderMuted(previousRecorder);
        }
    }

    internal static void EnforceTemporaryMute(CharacterVoiceHandler voiceHandler)
    {
        if (voiceHandler?.m_Recorder == null || !SavedVoiceRecorderStates.ContainsKey(voiceHandler.m_Recorder))
        {
            return;
        }

        ForceRecorderMuted(voiceHandler.m_Recorder);
    }

    private static void SaveVoiceRecorderState(Recorder recorder)
    {
        if (recorder == null || SavedVoiceRecorderStates.ContainsKey(recorder))
        {
            return;
        }

        SavedVoiceRecorderStates.Add(
            recorder,
            new VoiceRecorderState(recorder.TransmitEnabled, recorder.RecordingEnabled));
    }

    private static void RestoreSavedVoiceRecorderState(Recorder recorder)
    {
        if (recorder == null || !SavedVoiceRecorderStates.TryGetValue(recorder, out VoiceRecorderState state))
        {
            return;
        }

        SavedVoiceRecorderStates.Remove(recorder);
        recorder.RecordingEnabled = state.RecordingEnabled;
        recorder.TransmitEnabled = state.TransmitEnabled;
    }

    private static void ForceRecorderMuted(Recorder recorder)
    {
        recorder.TransmitEnabled = false;
        recorder.RecordingEnabled = false;
    }

    private static void EnableProximityVoiceTriggers(Character character)
    {
        foreach (ProximityVoiceTrigger trigger in character.GetComponentsInChildren<ProximityVoiceTrigger>(true))
        {
            if (!trigger.enabled)
            {
                trigger.enabled = true;
            }
        }
    }
}
