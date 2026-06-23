namespace TqkLibrary.StreamRelay.AspNetCore.Enums
{
    /// <summary>Egress wire message type (the second byte of every framed message, see plan §5).</summary>
    public enum WireMessageType : byte
    {
        /// <summary>Media init: codec id + extradata + dimensions + timebase for each stream.</summary>
        Init = 1,

        /// <summary>One compressed packet with pts/dts/duration/flags + payload.</summary>
        Packet = 2,

        /// <summary>Out-of-band control (e.g. end of stream).</summary>
        Control = 3,
    }
}
