using Elements.Core;
using FrooxEngine;
using VrmToResonitePackage.Vrm;

namespace VrmToResonitePackage;

/// <summary>
/// Converts VRM spring bones (hair/skirt/accessory physics) to Resonite DynamicBoneChains.
/// The parameter scales differ between the systems, so the mapping is calibrated around
/// each system's defaults rather than being exact physics.
/// </summary>
internal static class SpringBoneSetup
{
    public static void Apply(Slot root, VrmModel vrm)
    {
        if (vrm.SpringChains.Count == 0)
        {
            return;
        }
        Dictionary<string, Slot> slotsByName = SlotIndex.Build(root);
        var colliderCache = new Dictionary<int, List<IDynamicBoneCollider>>();
        int chains = 0;

        foreach (VrmSpringChain chain in vrm.SpringChains)
        {
            foreach (int rootNode in chain.RootNodes)
            {
                Slot boneRoot = ResolveNode(vrm, slotsByName, rootNode);
                if (boneRoot == null)
                {
                    UniLog.Warning($"揺れものボーンのノードが見つかりません: {vrm.GetNodeName(rootNode) ?? rootNode.ToString()}");
                    continue;
                }
                DynamicBoneChain dynamicChain = boneRoot.AttachComponent<DynamicBoneChain>();
                dynamicChain.SetupFromChildren(boneRoot);
                if (dynamicChain.Bones.Count == 0)
                {
                    dynamicChain.Destroy();
                    continue;
                }
                ApplyParameters(dynamicChain, chain, vrm);
                foreach (int colliderIndex in chain.ColliderIndices.Distinct())
                {
                    foreach (IDynamicBoneCollider collider in GetOrCreateColliders(vrm, slotsByName, colliderCache, colliderIndex))
                    {
                        dynamicChain.StaticColliders.Add().Target = collider;
                    }
                }
                chains++;
            }
        }
        UniLog.Log($"揺れものを {chains} チェーン設定しました。");
    }

    private static void ApplyParameters(DynamicBoneChain dynamicChain, VrmSpringChain chain, VrmModel vrm)
    {
        // VRM stiffness is roughly 0..4 (1 typical), Resonite's is 0..1 (0.2 default).
        dynamicChain.Stiffness.Value = MathX.Clamp(chain.Stiffness * 0.25f, 0.02f, 1f);
        // VRM dragForce 0..1 dampens motion; Resonite Inertia is the opposite notion.
        dynamicChain.Inertia.Value = MathX.Clamp((1f - chain.DragForce) * 0.4f, 0f, 0.6f);
        dynamicChain.BaseBoneRadius.Value = MathX.Max(0.001f, chain.HitRadius);
        float3 gravityDir = ConvertVector(chain.GravityDir, vrm.SpecVersionMajor);
        dynamicChain.Gravity.Value = gravityDir * (chain.GravityPower * 9.81f);
        dynamicChain.DynamicPlayerCollision.Value = true;
        dynamicChain.CollideWithOwnBody.Value = false;
        dynamicChain.IsGrabbable.Value = true;
    }

    private static List<IDynamicBoneCollider> GetOrCreateColliders(VrmModel vrm, Dictionary<string, Slot> slotsByName,
        Dictionary<int, List<IDynamicBoneCollider>> cache, int colliderIndex)
    {
        if (cache.TryGetValue(colliderIndex, out List<IDynamicBoneCollider> existing))
        {
            return existing;
        }
        var result = new List<IDynamicBoneCollider>();
        cache[colliderIndex] = result;
        if (colliderIndex < 0 || colliderIndex >= vrm.SpringColliders.Count)
        {
            return result;
        }
        VrmSpringCollider collider = vrm.SpringColliders[colliderIndex];
        Slot slot = ResolveNode(vrm, slotsByName, collider.NodeIndex);
        if (slot == null)
        {
            return result;
        }
        // Resonite's sphere collider has no offset field; the slot position is the offset.
        float3 offset = ConvertVector(collider.Offset, vrm.SpecVersionMajor);
        result.Add(CreateSphere(slot, offset, collider.Radius));
        if (collider.Tail.HasValue)
        {
            // VRM1 capsule: approximate with extra spheres along the capsule axis.
            float3 tail = ConvertVector(collider.Tail.Value, vrm.SpecVersionMajor);
            float3 axis = tail - offset;
            int segments = MathX.Clamp((int)MathX.Ceil(axis.Magnitude / MathX.Max(collider.Radius, 0.01f)), 1, 4);
            for (int i = 1; i <= segments; i++)
            {
                result.Add(CreateSphere(slot, offset + axis * ((float)i / segments), collider.Radius));
            }
        }
        return result;
    }

    private static IDynamicBoneCollider CreateSphere(Slot parent, float3 localPosition, float radius)
    {
        Slot colliderSlot = parent.AddSlot("VRM Collider");
        colliderSlot.LocalPosition = localPosition;
        DynamicBoneSphereCollider sphere = colliderSlot.AttachComponent<DynamicBoneSphereCollider>();
        sphere.Radius.Value = radius;
        return sphere;
    }

    private static Slot ResolveNode(VrmModel vrm, Dictionary<string, Slot> slotsByName, int nodeIndex)
    {
        string name = vrm.GetNodeName(nodeIndex);
        if (name == null)
        {
            return null;
        }
        slotsByName.TryGetValue(name, out Slot slot);
        return slot;
    }

    /// <summary>
    /// VRM0 stores spring data in Unity coordinates (UniVRM flips X on export),
    /// VRM1 stores glTF coordinates which Resonite's importer reads numerically as-is.
    /// </summary>
    private static float3 ConvertVector(System.Numerics.Vector3 v, int specVersionMajor)
    {
        return specVersionMajor == 0
            ? new float3(-v.X, v.Y, v.Z)
            : new float3(v.X, v.Y, v.Z);
    }
}
