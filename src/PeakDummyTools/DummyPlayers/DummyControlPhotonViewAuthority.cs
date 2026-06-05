using System.Collections.Generic;
using System.Reflection;
using Photon.Pun;

namespace PeakDummyTools.DummyPlayers;

internal static class DummyControlPhotonViewAuthority
{
    private sealed class PhotonViewAuthoritySnapshot
    {
        internal int ControllerActorNr { get; }

        internal bool IsMine { get; }

        internal PhotonViewAuthoritySnapshot(PhotonView view)
        {
            ControllerActorNr = view.ControllerActorNr;
            IsMine = view.IsMine;
        }
    }

    private static readonly Dictionary<int, PhotonViewAuthoritySnapshot> Snapshots = [];
    private static readonly FieldInfo? IsMineField = typeof(PhotonView).GetField(
        "<IsMine>k__BackingField",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static PhotonView? controlledView;

    internal static void AssignWriteControl(Character target, Character? originalLocalCharacter)
    {
        if (target == null || target == originalLocalCharacter)
        {
            RestoreControlledView();
            return;
        }

        if (!TryGetCharacterView(target, out PhotonView view))
        {
            RestoreControlledView();
            Plugin.Log.LogWarning($"Unable to take over PhotonView writing for {target.characterName} because no character view was found.");
            return;
        }

        if (controlledView != null && controlledView != view)
        {
            RestoreControlledView();
        }

        TakeWriteControl(view);
    }

    internal static void RestoreControlledView()
    {
        PhotonView? view = controlledView;
        controlledView = null;
        if (view == null)
        {
            return;
        }

        RestoreView(view);
    }

    internal static void HandleCharacterRemoved(Character character)
    {
        if (character == null || !TryGetCharacterView(character, out PhotonView view))
        {
            return;
        }

        int viewId = view.ViewID;
        Snapshots.Remove(viewId);
        if (controlledView == view || controlledView != null && controlledView.ViewID == viewId)
        {
            controlledView = null;
        }
    }

    private static void TakeWriteControl(PhotonView view)
    {
        if (PhotonNetwork.LocalPlayer == null)
        {
            return;
        }

        int viewId = view.ViewID;
        if (!Snapshots.ContainsKey(viewId))
        {
            Snapshots.Add(viewId, new PhotonViewAuthoritySnapshot(view));
        }

        controlledView = view;
        int localActorNumber = PhotonNetwork.LocalPlayer.ActorNumber;
        if (view.ControllerActorNr != localActorNumber)
        {
            view.ControllerActorNr = localActorNumber;
        }

        if (!view.IsMine)
        {
            SetIsMine(view, true);
        }
    }

    private static void RestoreView(PhotonView view)
    {
        int viewId = view.ViewID;
        if (!Snapshots.TryGetValue(viewId, out PhotonViewAuthoritySnapshot snapshot))
        {
            return;
        }

        Snapshots.Remove(viewId);
        view.ControllerActorNr = snapshot.ControllerActorNr;
        if (view.IsMine != snapshot.IsMine)
        {
            SetIsMine(view, snapshot.IsMine);
        }
    }

    private static void SetIsMine(PhotonView view, bool isMine)
    {
        if (IsMineField == null)
        {
            Plugin.Log.LogWarning("Unable to update PhotonView IsMine backing field because the field was not found.");
            return;
        }

        IsMineField.SetValue(view, isMine);
    }

    private static bool TryGetCharacterView(Character character, out PhotonView view)
    {
        view = null!;
        if (character == null)
        {
            return false;
        }

        view = character.refs != null && character.refs.view != null
            ? character.refs.view
            : character.photonView;

        return view != null;
    }
}
