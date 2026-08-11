namespace CharacterAnimationCli.Unity.Command
{
    public enum command_kind
    {
        Unknown = 0,
        GeneratePlayableClip = 1,
        CancelPlayableClipGeneration = 2,
        GenerateNavMeshTrackClips = 3,
        GenerateAnimationAsset = 4
    }
}
