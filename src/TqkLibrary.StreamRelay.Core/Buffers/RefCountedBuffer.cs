using System;
using System.Buffers;
using System.Threading;

namespace TqkLibrary.StreamRelay.Buffers
{
    /// <summary>
    /// A pooled, reference-counted byte buffer. Every packet payload lives here so the same bytes can be
    /// retained by the GOP buffer and sent to many viewers without copying. The backing array is returned
    /// to its <see cref="ArrayPool{T}"/> when the reference count reaches zero.
    /// </summary>
    /// <remarks>
    /// <see cref="AddRef"/> must only be called while the caller already holds a live reference; this avoids
    /// the classic resurrection race with <see cref="Release"/>.
    /// </remarks>
    public sealed class RefCountedBuffer
    {
        readonly ArrayPool<byte> _pool;
        byte[] _array;
        readonly int _length;
        int _refCount;

        RefCountedBuffer(ArrayPool<byte> pool, byte[] array, int length)
        {
            _pool = pool;
            _array = array;
            _length = length;
            _refCount = 1;
        }

        /// <summary>Rent a buffer of exactly <paramref name="length"/> usable bytes with an initial ref count of 1.</summary>
        public static RefCountedBuffer Rent(int length, ArrayPool<byte>? pool = null)
        {
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            pool ??= ArrayPool<byte>.Shared;
            byte[] array = pool.Rent(length == 0 ? 1 : length);
            return new RefCountedBuffer(pool, array, length);
        }

        /// <summary>Length of the usable payload (not the rented array's capacity).</summary>
        public int Length => _length;

        /// <summary>Writable view used to fill the payload immediately after renting.</summary>
        public Span<byte> WritableSpan => new Span<byte>(_array, 0, _length);

        /// <summary>Read-only view of the payload, safe to pass to socket send APIs.</summary>
        public ReadOnlyMemory<byte> Memory => new ReadOnlyMemory<byte>(_array, 0, _length);

        /// <summary>Add one reference. Call only while already holding a live reference.</summary>
        public RefCountedBuffer AddRef()
        {
            int updated = Interlocked.Increment(ref _refCount);
            if (updated <= 1)
                throw new ObjectDisposedException(nameof(RefCountedBuffer), "AddRef on an already-released buffer.");
            return this;
        }

        /// <summary>Release one reference; returns the array to the pool when it hits zero.</summary>
        public void Release()
        {
            int updated = Interlocked.Decrement(ref _refCount);
            if (updated == 0)
            {
                byte[] array = Interlocked.Exchange(ref _array, null!);
                if (array != null)
                    _pool.Return(array);
            }
            else if (updated < 0)
            {
                throw new InvalidOperationException("RefCountedBuffer released more times than referenced.");
            }
        }
    }
}
