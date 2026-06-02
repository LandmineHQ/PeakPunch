using UnityEngine;

namespace BuddyClimb.Gameplay;

internal static class CarryInteractionProxy
{
    private const string ProxyName = "BuddyClimbInteractionProxy";
    private const float ProxyRadius = 0.75f;

    internal static void Enable(Character character)
    {
        if (character == null)
        {
            return;
        }

        Transform parent = GetProxyParent(character);
        if (parent == null)
        {
            return;
        }

        Transform existingProxy = parent.Find(ProxyName);
        if (existingProxy != null)
        {
            existingProxy.gameObject.SetActive(true);
            return;
        }

        GameObject proxy = new(ProxyName);
        proxy.transform.SetParent(parent, false);
        proxy.transform.localPosition = Vector3.zero;
        proxy.transform.localRotation = Quaternion.identity;
        proxy.transform.localScale = Vector3.one;

        int characterLayer = LayerMask.NameToLayer("Character");
        proxy.layer = characterLayer >= 0 ? characterLayer : character.gameObject.layer;

        SphereCollider collider = proxy.AddComponent<SphereCollider>();
        collider.isTrigger = true;
        collider.radius = ProxyRadius;
    }

    internal static void Disable(Character character)
    {
        if (character == null)
        {
            return;
        }

        Transform parent = GetProxyParent(character);
        if (parent.Find(ProxyName) is Transform proxy)
        {
            Object.Destroy(proxy.gameObject);
        }
    }

    private static Transform GetProxyParent(Character character)
    {
        Bodypart torso = character.GetBodypart(BodypartType.Torso);
        if (torso != null)
        {
            return torso.transform;
        }

        return character.transform;
    }
}
