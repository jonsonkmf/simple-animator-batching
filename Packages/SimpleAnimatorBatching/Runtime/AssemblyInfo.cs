using System.Runtime.CompilerServices;

// Allows the Editor assembly (baker, custom inspectors) to access internal members
// of Runtime types (e.g. CrowdArchetypeAsset's baked-data fields) without making
// them part of the public runtime API surface.
[assembly: InternalsVisibleTo("SimpleAnimatorBatching.Editor")]
