using System;
using TqkLibrary.StreamRelay.Buffers;

namespace TqkLibrary.StreamRelay.Test.Fakes
{
    /// <summary>
    /// Probes a <see cref="RefCountedBuffer"/>'s liveness without touching Core internals. A fully released
    /// buffer (refcount 0, array returned to the pool) rejects <see cref="RefCountedBuffer.AddRef"/> with
    /// <see cref="ObjectDisposedException"/>; a live buffer accepts it (and is balanced back immediately).
    /// </summary>
    internal static class RefCountAssert
    {
        public static bool IsFullyReleased(RefCountedBuffer buffer)
        {
            try
            {
                buffer.AddRef();   // succeeds only while a live ref exists
                buffer.Release();  // balance the probe
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }
    }
}
