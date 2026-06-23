using TqkLibrary.StreamRelay.Enums;

namespace TqkLibrary.StreamRelay.Models
{
    /// <summary>
    /// One item in a subscriber's send queue. It is either a media <see cref="MediaInit"/> (sent first and
    /// re-sent on every resync) or a <see cref="RelayPacket"/>. Carrying both in a single ordered channel
    /// makes "init then GOP then live" atomic and removes any cross-thread ordering race.
    /// </summary>
    public readonly struct SubscriberMessage
    {
        SubscriberMessage(SubscriberMessageKind kind, MediaInit? init, RelayPacket? packet)
        {
            Kind = kind;
            Init = init;
            Packet = packet;
        }

        public SubscriberMessageKind Kind { get; }

        /// <summary>Non-null when <see cref="Kind"/> is <see cref="SubscriberMessageKind.Init"/>.</summary>
        public MediaInit? Init { get; }

        /// <summary>
        /// Non-null when <see cref="Kind"/> is <see cref="SubscriberMessageKind.Packet"/>. Carries one ref
        /// owned by the subscriber; whoever consumes it must release it.
        /// </summary>
        public RelayPacket? Packet { get; }

        public static SubscriberMessage ForInit(MediaInit init) =>
            new SubscriberMessage(SubscriberMessageKind.Init, init, null);

        public static SubscriberMessage ForPacket(RelayPacket packet) =>
            new SubscriberMessage(SubscriberMessageKind.Packet, null, packet);
    }
}
