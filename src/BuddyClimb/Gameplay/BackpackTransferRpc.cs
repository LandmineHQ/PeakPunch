using Photon.Pun;
using UnityEngine;

namespace BuddyClimb.Gameplay;

internal sealed class BackpackTransferRpc : MonoBehaviour
{
    internal static BackpackTransferRpc Ensure(Character character)
    {
        BackpackTransferRpc component = character.GetComponent<BackpackTransferRpc>();
        if (component == null)
        {
            component = character.gameObject.AddComponent<BackpackTransferRpc>();
            character.photonView.RefreshRpcMonoBehaviourCache();
        }

        return component;
    }

    internal void RequestDropCarriedBackpackAndTransfer(PhotonView carrierView, PhotonView carriedView)
    {
        PhotonView photonView = GetComponent<PhotonView>();
        if (photonView == null)
        {
            Plugin.Log.LogWarning("Unable to request backpack transfer because the character PhotonView is unavailable.");
            return;
        }

        photonView.RPC(
            nameof(RPCA_DropCarriedBackpackAndTransfer),
            RpcTarget.MasterClient,
            carrierView,
            carriedView);
    }

    [PunRPC]
    private void RPCA_DropCarriedBackpackAndTransfer(PhotonView carrierView, PhotonView carriedView)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            return;
        }

        Character? carrier = carrierView != null ? carrierView.GetComponent<Character>() : null;
        Character? carried = carriedView != null ? carriedView.GetComponent<Character>() : null;
        if (carrier == null || carried == null)
        {
            Plugin.Log.LogWarning("Unable to complete backpack transfer because a character PhotonView is invalid.");
            return;
        }

        BackpackCarryTransfer.TryDropCarriedBackpackAndTransferOnMaster(carrier, carried);
    }
}
