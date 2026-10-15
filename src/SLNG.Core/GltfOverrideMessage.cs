using System.Collections.Generic;

namespace SLNG.Core;

/// <summary>One GenericStreamingMessage 0x4175, parsed. REPLACES the object's whole override set.</summary>
public sealed record GltfOverrideMessage(uint LocalId, IReadOnlyDictionary<int, GltfMaterialOverride> Faces);
