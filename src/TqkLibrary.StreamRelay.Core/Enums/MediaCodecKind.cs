namespace TqkLibrary.StreamRelay.Enums
{
    /// <summary>
    /// Logical kind of a media stream inside a container. Kept independent of any FFmpeg type so the
    /// Core library carries no FFmpeg dependency.
    /// </summary>
    public enum MediaCodecKind
    {
        Unknown = 0,
        Video = 1,
        Audio = 2,
        Subtitle = 3,
        Data = 4,
    }
}
