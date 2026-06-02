using Photon.Pun;
using UnityEngine;

namespace BuddyClimb.Gameplay;

internal sealed class BackpackSnapshotDropRpc : MonoBehaviour
{
    internal static BackpackSnapshotDropRpc Ensure(Character character)
    {
        BackpackSnapshotDropRpc component = character.GetComponent<BackpackSnapshotDropRpc>();
        if (component == null)
        {
            component = character.gameObject.AddComponent<BackpackSnapshotDropRpc>();
            character.photonView.RefreshRpcMonoBehaviourCache();
        }

        return component;
    }

    internal void DropBackpackSnapshot(string prefabName, ItemInstanceData backpackData, Vector3 dropPosition)
    {
        PhotonView photonView = GetComponent<PhotonView>();
        if (photonView == null)
        {
            Plugin.Log.LogWarning("Unable to drop backpack snapshot because the character PhotonView is unavailable.");
            return;
        }

        photonView.RPC(
            nameof(RPCA_DropBuddyClimbBackpackSnapshot),
            RpcTarget.MasterClient,
            prefabName,
            backpackData,
            dropPosition);
    }

    [PunRPC]
    private void RPCA_DropBuddyClimbBackpackSnapshot(
        string prefabName,
        ItemInstanceData backpackData,
        Vector3 dropPosition)
    {
        if (!PhotonNetwork.IsMasterClient || string.IsNullOrEmpty(prefabName))
        {
            return;
        }

        PhotonView droppedBackpack = PhotonNetwork.Instantiate(
                $"0_Items/{prefabName}",
                dropPosition,
                Quaternion.identity)
            .GetComponent<PhotonView>();

        droppedBackpack.RPC(
            nameof(Item.SetItemInstanceDataRPC),
            RpcTarget.All,
            backpackData);

        droppedBackpack.RPC(
            nameof(Item.SetKinematicRPC),
            RpcTarget.All,
            false,
            droppedBackpack.transform.position,
            droppedBackpack.transform.rotation);

        Character? character = GetComponent<Character>();
        CharacterItems? characterItems = character?.refs.items;
        if (characterItems != null)
        {
            characterItems.droppedItems.Add(droppedBackpack);
        }
    }
}
