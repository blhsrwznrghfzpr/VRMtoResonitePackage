using Elements.Core;
using System.Text;
using System.Text.Json.Nodes;

namespace VrmToResonitePackage.Vrm;

/// <summary>
/// Rewrites the GLB JSON chunk before handing the file to Resonite's importer.
///
/// Older UniVRM exporters (e.g. UniGLTF-1.28 / AliciaSolid) store morph target names
/// only as primitives[].extras.targetNames, which Assimp does not read — every
/// blendshape comes out unnamed. FrooxEngine's duplicate-name guard treats empty
/// names as "not present" (MeshX.HasBlendShape returns false for empty strings),
/// so the second unnamed blendshape crashes the import with
/// "BlendShape with given key already exists".
///
/// This preprocessor guarantees every mesh with morph targets has unique, non-empty
/// mesh.extras.targetNames (taken from primitive extras when available, generated
/// otherwise).
/// </summary>
internal static class GlbPreprocessor
{
    private const uint GlbMagic = 0x46546C67;   // "glTF"
    private const uint JsonChunkType = 0x4E4F534A; // "JSON"
    private const uint BinChunkType = 0x004E4942; // "BIN\0"

    /// <summary>
    /// Copies the VRM to <paramref name="targetPath"/> as an import-ready GLB.
    /// Returns true when a Y180 orientation bake was applied (VRM0 only), which the caller
    /// records on the <see cref="VrmModel"/> so collider offsets are converted correctly.
    /// </summary>
    public static bool CreateImportableGlb(string sourcePath, string targetPath)
    {
        byte[] data = File.ReadAllBytes(sourcePath);
        if (data.Length < 12 || BitConverter.ToUInt32(data, 0) != GlbMagic)
        {
            throw new InvalidDataException("GLBファイルではありません（マジックナンバー不一致）。");
        }
        uint containerVersion = BitConverter.ToUInt32(data, 4);

        // Decode the chunk list.
        var chunks = new List<(uint type, int offset, int length)>();
        int position = 12;
        while (position + 8 <= data.Length)
        {
            int length = checked((int)BitConverter.ToUInt32(data, position));
            uint type = BitConverter.ToUInt32(data, position + 4);
            chunks.Add((type, position + 8, length));
            position += 8 + length;
        }

        int jsonIndex = chunks.FindIndex(c => c.type == JsonChunkType);
        if (jsonIndex < 0)
        {
            throw new InvalidDataException("GLBのJSONチャンクが見つかりません。");
        }
        (uint _, int jsonOffset, int jsonLength) = chunks[jsonIndex];

        int binIndex = chunks.FindIndex(c => c.type == BinChunkType);
        int binOffset = binIndex >= 0 ? chunks[binIndex].offset : -1;
        int binLength = binIndex >= 0 ? chunks[binIndex].length : 0;

        JsonNode root = JsonNode.Parse(new ReadOnlySpan<byte>(data, jsonOffset, jsonLength));
        bool changed = false;
        bool baked = false;
        if (root != null)
        {
            changed |= FixTargetNames(root);
            changed |= FixMorphTargetAttributes(root);
            // VRM0 imports facing -Z, which forces VRIK to spin CenteredRoot 180°. Baking a
            // Y180 (origin-centered) into the glTF makes the imported model face +Z with a
            // zero CenteredRoot rotation. Combined with the importer's X mirror and UniVRM's
            // ReverseZ export this collapses to identity, so engine space becomes Unity space.
            baked = BakeVrm0Y180(root, data, binOffset, binLength);
            changed |= baked;
        }
        if (!changed)
        {
            File.Copy(sourcePath, targetPath, overwrite: true);
            return false;
        }

        // Re-encode the JSON chunk, padded with spaces to 4-byte alignment per spec.
        byte[] jsonBytes = Encoding.UTF8.GetBytes(root.ToJsonString());
        int paddedJsonLength = (jsonBytes.Length + 3) & ~3;

        using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);
        long totalLength = 12 + 8 + paddedJsonLength;
        for (int i = 0; i < chunks.Count; i++)
        {
            if (i != jsonIndex)
            {
                totalLength += 8 + chunks[i].length;
            }
        }
        writer.Write(GlbMagic);
        writer.Write(containerVersion);
        writer.Write(checked((uint)totalLength));

        writer.Write((uint)paddedJsonLength);
        writer.Write(JsonChunkType);
        writer.Write(jsonBytes);
        for (int i = jsonBytes.Length; i < paddedJsonLength; i++)
        {
            writer.Write((byte)0x20);
        }

        foreach ((uint type, int offset, int length) in chunks)
        {
            if (type == JsonChunkType)
            {
                continue;
            }
            writer.Write((uint)length);
            writer.Write(type);
            writer.Write(data, offset, length);
        }
        return baked;
    }

    /// <summary>Returns true when the JSON was modified.</summary>
    private static bool FixTargetNames(JsonNode root)
    {
        if (root["meshes"] is not JsonArray meshes)
        {
            return false;
        }
        bool changed = false;
        foreach (JsonNode meshNode in meshes)
        {
            if (meshNode is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray primitives ||
                primitives.Count == 0)
            {
                continue;
            }
            int targetCount = 0;
            if (primitives[0] is JsonObject firstPrimitive &&
                firstPrimitive["targets"] is JsonArray targets)
            {
                targetCount = targets.Count;
            }
            if (targetCount == 0)
            {
                continue;
            }

            List<string> existing = ReadNames(mesh["extras"]) ?? ReadPrimitiveNames(primitives);
            var unique = new List<string>(targetCount);
            var used = new HashSet<string>(StringComparer.Ordinal);
            bool meshChanged = existing == null || existing.Count < targetCount;
            for (int i = 0; i < targetCount; i++)
            {
                string name = existing != null && i < existing.Count ? existing[i] : null;
                if (string.IsNullOrEmpty(name))
                {
                    name = $"morph_{i}";
                    meshChanged = true;
                }
                // Same disambiguation FrooxEngine itself uses for duplicate names.
                while (!used.Add(name))
                {
                    name += "_";
                    meshChanged = true;
                }
                unique.Add(name);
            }
            // mesh.extras.targetNames missing entirely also counts as a change.
            if (mesh["extras"] is not JsonObject || ReadNames(mesh["extras"]) == null)
            {
                meshChanged = true;
            }
            if (!meshChanged)
            {
                continue;
            }

            if (mesh["extras"] is not JsonObject extras)
            {
                extras = new JsonObject();
                mesh["extras"] = extras;
            }
            var array = new JsonArray();
            foreach (string name in unique)
            {
                array.Add(JsonValue.Create(name));
            }
            extras["targetNames"] = array;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// FrooxEngine merges all primitives of a node into one mesh and assumes morph
    /// target attributes are uniform across primitives: it takes HasNormals from the
    /// first primitive's target and then indexes the other primitives' normal lists,
    /// crashing when a primitive lacks NORMAL on that target. Combined/merged VRMs
    /// (e.g. face-tracking bodies) frequently mix per-target NORMAL presence, so this
    /// strips NORMAL/TANGENT from targets where the primitives disagree.
    /// It also trims target counts to the minimum across primitives for the same
    /// reason (the merge loop iterates the first primitive's count).
    /// </summary>
    private static bool FixMorphTargetAttributes(JsonNode root)
    {
        if (root["meshes"] is not JsonArray meshes)
        {
            return false;
        }
        bool changed = false;
        foreach (JsonNode meshNode in meshes)
        {
            if (meshNode is not JsonObject mesh ||
                mesh["primitives"] is not JsonArray primitives ||
                primitives.Count < 2)
            {
                continue;
            }
            var targetArrays = new List<JsonArray>();
            foreach (JsonNode primitive in primitives)
            {
                if (primitive is JsonObject primitiveObject &&
                    primitiveObject["targets"] is JsonArray targets)
                {
                    targetArrays.Add(targets);
                }
                else
                {
                    targetArrays.Add(null);
                }
            }
            if (targetArrays.All(t => t == null || t.Count == 0))
            {
                continue;
            }

            // Uniform target count across primitives (glTF requires it, tools violate it).
            int minCount = targetArrays.Min(t => t?.Count ?? 0);
            foreach (JsonArray targets in targetArrays)
            {
                if (targets == null)
                {
                    continue;
                }
                while (targets.Count > minCount)
                {
                    targets.RemoveAt(targets.Count - 1);
                    changed = true;
                }
            }

            // Per-target attribute consistency.
            foreach (string attribute in new[] { "NORMAL", "TANGENT" })
            {
                for (int k = 0; k < minCount; k++)
                {
                    bool all = true;
                    bool any = false;
                    foreach (JsonArray targets in targetArrays)
                    {
                        bool has = targets?[k] is JsonObject target && target[attribute] != null;
                        all &= has;
                        any |= has;
                    }
                    if (!any || all)
                    {
                        continue;
                    }
                    foreach (JsonArray targets in targetArrays)
                    {
                        if (targets?[k] is JsonObject target && target[attribute] != null)
                        {
                            target.Remove(attribute);
                            changed = true;
                        }
                    }
                }
            }
        }
        return changed;
    }

    /// <summary>
    /// Bakes a Y180 rotation R = diag(-1, 1, -1) (origin-centered) into a VRM0 glTF so the
    /// imported model faces +Z. Only safe — and only applied — when the file is a normalized,
    /// rotation-free VRM0; otherwise logs a warning and skips.
    ///
    /// Math: in a hierarchy with no node rotations, every joint's world transform is just its
    /// world translation T(p). Applying R to every node translation moves jointWorld to R·p,
    /// and left-multiplying each inverse bind matrix by R (IBM' = R·IBM) makes the skinned
    /// vertex world coordinates exactly R·(original world coordinate). Vertex, normal and morph
    /// data need no change. Returns true when the bake was applied.
    /// </summary>
    private static bool BakeVrm0Y180(JsonNode root, byte[] data, int binOffset, int binLength)
    {
        if (root["extensions"] is not JsonObject extensions || extensions["VRM"] == null)
        {
            return false; // Not VRM0 — bake is VRM0-specific.
        }

        if (!CanBakeVrm0(root, out string reason))
        {
            UniLog.Warning($"VRM0の向き正規化をスキップしました（{reason}）。");
            return false;
        }

        // 1) Mirror translations: [x, y, z] -> [-x, y, -z].
        if (root["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode nodeNode in nodes)
            {
                if (nodeNode is JsonObject node && node["translation"] is JsonArray t && t.Count == 3)
                {
                    t[0] = JsonValue.Create(-t[0].GetValue<float>());
                    t[2] = JsonValue.Create(-t[2].GetValue<float>());
                }
            }
        }

        // 2) Left-multiply each skin's inverseBindMatrices by R. glTF mat4 is column-major,
        //    so R = diag(-1,1,-1) negates rows 0 and 2 = column-major indices 0,4,8,12 and
        //    2,6,10,14. Edit the float群 in place in the binary chunk.
        if (root["skins"] is JsonArray skins)
        {
            JsonArray accessors = root["accessors"] as JsonArray;
            JsonArray bufferViews = root["bufferViews"] as JsonArray;
            foreach (JsonNode skinNode in skins)
            {
                if (skinNode is not JsonObject skin ||
                    skin["inverseBindMatrices"] is not JsonValue ibmRef)
                {
                    continue;
                }
                int accessorIndex = ibmRef.GetValue<int>();
                JsonObject accessor = accessors?[accessorIndex] as JsonObject;
                int count = accessor?["count"]?.GetValue<int>() ?? 0;
                int bufferViewIndex = accessor?["bufferView"]?.GetValue<int>() ?? -1;
                int accessorByteOffset = accessor?["byteOffset"]?.GetValue<int>() ?? 0;
                JsonObject bufferView = bufferViewIndex >= 0 ? bufferViews?[bufferViewIndex] as JsonObject : null;
                int bufferViewByteOffset = bufferView?["byteOffset"]?.GetValue<int>() ?? 0;
                if (count <= 0 || bufferView == null || binOffset < 0)
                {
                    continue;
                }

                int matStart = binOffset + bufferViewByteOffset + accessorByteOffset;
                for (int m = 0; m < count; m++)
                {
                    int baseOffset = matStart + m * 64; // 16 floats * 4 bytes
                    foreach (int floatIndex in new[] { 0, 4, 8, 12, 2, 6, 10, 14 })
                    {
                        int byteIndex = baseOffset + floatIndex * 4;
                        float value = BitConverter.ToSingle(data, byteIndex);
                        BitConverter.GetBytes(-value).CopyTo(data, byteIndex);
                    }
                }
            }
        }

        UniLog.Log("VRM0の向きを+Zに正規化しました（CenteredRoot回転0）。");
        return true;
    }

    /// <summary>
    /// Verifies the strict preconditions under which the Y180 bake's "rotation-free hierarchy"
    /// math holds. Sets <paramref name="reason"/> to a Japanese explanation when it does not.
    /// </summary>
    private static bool CanBakeVrm0(JsonNode root, out string reason)
    {
        // Animations would be baked against the old orientation.
        if (root["animations"] is JsonArray animations && animations.Count > 0)
        {
            reason = "アニメーションを含むため";
            return false;
        }

        if (root["nodes"] is JsonArray nodes)
        {
            foreach (JsonNode nodeNode in nodes)
            {
                if (nodeNode is not JsonObject node)
                {
                    continue;
                }
                // A node matrix would not compose cleanly with our translation/IBM edit.
                if (node["matrix"] != null)
                {
                    reason = "ノードにmatrixが指定されているため";
                    return false;
                }
                // The math assumes every node rotation is identity.
                if (node["rotation"] is JsonArray rotation && rotation.Count == 4)
                {
                    if (MathX.Abs(rotation[0].GetValue<float>()) >= 1e-4f ||
                        MathX.Abs(rotation[1].GetValue<float>()) >= 1e-4f ||
                        MathX.Abs(rotation[2].GetValue<float>()) >= 1e-4f)
                    {
                        reason = "ノードに非ゼロ回転があるため";
                        return false;
                    }
                }
                // A mesh node without a skin has un-rotated vertices; the bake would not turn them.
                if (node["mesh"] != null && node["skin"] == null)
                {
                    reason = "スキンを持たないメッシュノードがあるため";
                    return false;
                }
            }
        }

        // Every skin's IBM accessor must be a plain float MAT4 (no sparse) so the in-place
        // binary edit applies cleanly.
        JsonArray accessors = root["accessors"] as JsonArray;
        if (root["skins"] is JsonArray skins)
        {
            foreach (JsonNode skinNode in skins)
            {
                if (skinNode is not JsonObject skin)
                {
                    continue;
                }
                if (skin["inverseBindMatrices"] is not JsonValue ibmRef)
                {
                    reason = "skinにinverseBindMatricesが無いため";
                    return false;
                }
                int accessorIndex = ibmRef.GetValue<int>();
                if (accessors?[accessorIndex] is not JsonObject accessor)
                {
                    reason = "inverseBindMatricesのaccessorが見つからないため";
                    return false;
                }
                int componentType = accessor["componentType"]?.GetValue<int>() ?? 0;
                string type = accessor["type"]?.GetValue<string>();
                if (componentType != 5126 || type != "MAT4" || accessor["sparse"] != null)
                {
                    reason = "inverseBindMatricesがfloat MAT4(非sparse)でないため";
                    return false;
                }
                // A byteStride other than the tight 64-byte mat4 size would break the in-place
                // packing assumption (the spec allows interleaving, though MAT4 IBMs never do).
                int bufferViewIndex = accessor["bufferView"]?.GetValue<int>() ?? -1;
                JsonObject bufferView = bufferViewIndex >= 0
                    ? (root["bufferViews"] as JsonArray)?[bufferViewIndex] as JsonObject : null;
                int byteStride = bufferView?["byteStride"]?.GetValue<int>() ?? 0;
                if (byteStride != 0 && byteStride != 64)
                {
                    reason = "inverseBindMatricesにbyteStrideが指定されているため";
                    return false;
                }
            }
        }

        reason = null;
        return true;
    }

    private static List<string> ReadNames(JsonNode extras)
    {
        if (extras is JsonObject extrasObject &&
            extrasObject["targetNames"] is JsonArray names)
        {
            return names.Select(n => n?.GetValue<string>() ?? "").ToList();
        }
        return null;
    }

    private static List<string> ReadPrimitiveNames(JsonArray primitives)
    {
        foreach (JsonNode primitive in primitives)
        {
            if (primitive is JsonObject primitiveObject)
            {
                List<string> names = ReadNames(primitiveObject["extras"]);
                if (names != null)
                {
                    return names;
                }
            }
        }
        return null;
    }
}
