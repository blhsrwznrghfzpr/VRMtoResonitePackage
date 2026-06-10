using Elements.Assets;
using Elements.Core;

namespace VrmToResonitePackage;

/// <summary>
/// Diagnostic utility: decodes a .resonitepackage and prints the saved slot/component
/// structure without launching the engine. Used to verify converter output.
/// </summary>
internal static class PackageInspector
{
    public static int Inspect(string packagePath, bool verbose)
    {
        using RecordPackage package = RecordPackage.Decode(packagePath);
        SkyFrost.Base.Record record = package.MainRecord;
        if (record == null)
        {
            Console.Error.WriteLine("R-Main.record が見つかりません。");
            return 1;
        }
        Console.WriteLine($"名前: {record.Name}  所有者: {record.OwnerId}");
        Console.WriteLine($"アセット数: {package.AssetCount}");

        string signature = RecordPackage.GetAssetSignature(new Uri(record.AssetURI));
        using Stream stream = package.ReadAsset(signature);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        DataTreeDictionary root = DataTreeConverter.LoadAuto(memory);
        if (root == null)
        {
            Console.Error.WriteLine("メインアセットをDataTreeとしてデコードできませんでした。");
            return 1;
        }

        // Type table: list of type names referenced by index from components.
        var typeNames = new List<string>();
        if (root.TryGetNode("Types") is DataTreeList typesList)
        {
            foreach (DataTreeNode node in typesList.Children)
            {
                typeNames.Add((node as DataTreeValue)?.Extract<string>() ?? "?");
            }
        }

        var componentCounts = new Dictionary<string, int>();
        int slotCount = 0;

        void WalkSlot(DataTreeDictionary slot, int depth)
        {
            slotCount++;
            string name = ExtractField<string>(slot.TryGetNode("Name")) ?? "(unnamed)";
            if (verbose || depth <= 2)
            {
                Console.WriteLine($"{new string(' ', depth * 2)}- {name}");
            }
            if (slot.TryGetNode("Components") is DataTreeDictionary componentsDict &&
                componentsDict.TryGetNode("Data") is DataTreeList componentList)
            {
                foreach (DataTreeNode component in componentList.Children)
                {
                    if (component is not DataTreeDictionary componentDict)
                    {
                        continue;
                    }
                    string typeName = ResolveType(componentDict.TryGetNode("Type"), typeNames);
                    componentCounts[typeName] = componentCounts.GetValueOrDefault(typeName) + 1;
                }
            }
            if (slot.TryGetNode("Children") is DataTreeList children)
            {
                foreach (DataTreeNode child in children.Children)
                {
                    if (child is DataTreeDictionary childDict)
                    {
                        WalkSlot(childDict, depth + 1);
                    }
                }
            }
        }

        if (root.TryGetNode("Object") is DataTreeDictionary objectRoot)
        {
            Console.WriteLine();
            Console.WriteLine("スロットツリー (深さ2まで):");
            WalkSlot(objectRoot, 0);
        }

        // Collected asset providers (materials, textures, meshes) are stored separately.
        var assetCounts = new Dictionary<string, int>();
        if (root.TryGetNode("Assets") is DataTreeList assets)
        {
            foreach (DataTreeNode asset in assets.Children)
            {
                if (asset is DataTreeDictionary assetDict)
                {
                    string typeName = ResolveType(assetDict.TryGetNode("Type"), typeNames);
                    assetCounts[typeName] = assetCounts.GetValueOrDefault(typeName) + 1;
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"スロット数: {slotCount}");
        Console.WriteLine("コンポーネント数:");
        foreach ((string type, int count) in componentCounts.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {count,5} x {Shorten(type)}");
        }
        Console.WriteLine();
        Console.WriteLine("収集アセット (Assetsノード):");
        foreach ((string type, int count) in assetCounts.OrderByDescending(kv => kv.Value))
        {
            Console.WriteLine($"  {count,5} x {Shorten(type)}");
        }
        return 0;
    }

    private static string ResolveType(DataTreeNode typeNode, List<string> typeNames)
    {
        if (typeNode is DataTreeValue value)
        {
            object raw = value.Extract<object>();
            if (raw is int index && index >= 0 && index < typeNames.Count)
            {
                return typeNames[index];
            }
            if (raw is long longIndex && longIndex >= 0 && longIndex < typeNames.Count)
            {
                return typeNames[(int)longIndex];
            }
            if (raw is string s)
            {
                return s;
            }
        }
        return "?";
    }

    private static T ExtractField<T>(DataTreeNode node)
    {
        // Sync fields are saved as dictionaries with a "Data" entry.
        if (node is DataTreeDictionary dict)
        {
            node = dict.TryGetNode("Data");
        }
        if (node is DataTreeValue value)
        {
            try
            {
                return value.Extract<T>();
            }
            catch
            {
                return default;
            }
        }
        return default;
    }

    private static string Shorten(string typeName)
    {
        // Strip assembly-qualified noise, keep readable generic names.
        int bracket = typeName.IndexOf('[');
        string core = bracket > 0 ? typeName[..bracket] : typeName;
        return core.Length > 100 ? core[..100] : typeName.Length > 120 ? typeName[..120] : typeName;
    }
}
