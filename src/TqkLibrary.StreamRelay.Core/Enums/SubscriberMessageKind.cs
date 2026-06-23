namespace TqkLibrary.StreamRelay.Enums
{
    /// <summary>Discriminator for <see cref="Models.SubscriberMessage"/>.</summary>
    public enum SubscriberMessageKind
    {
        /// <summary>Media init (codec/extradata); sent first and re-sent on every resync.</summary>
        Init = 0,

        /// <summary>A compressed media packet.</summary>
        Packet = 1,
    }
}
