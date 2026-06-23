using System;
using System.Threading;
using System.Threading.Tasks;
using TqkLibrary.StreamRelay.Models;
using Xunit;

namespace TqkLibrary.StreamRelay.Test
{
    public class StreamRegistryTests
    {
        [Fact]
        public void GetOrCreateForIngest_IsIdempotent_PerStreamId()
        {
            using var registryAsync = new RegistryScope(new RelaySessionOptions());
            StreamRegistry registry = registryAsync.Registry;

            Guid id = Guid.NewGuid();
            StreamSession a = registry.GetOrCreateForIngest(id);
            StreamSession b = registry.GetOrCreateForIngest(id);
            Assert.Same(a, b);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public void TryGet_ReturnsFalse_WhenNoStream()
        {
            using var scope = new RegistryScope(new RelaySessionOptions());
            Assert.False(scope.Registry.TryGet(Guid.NewGuid(), out _));
        }

        [Fact]
        public async Task SweepIdle_RemovesEndedIdleSession_WithNoViewers()
        {
            var options = new RelaySessionOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) };
            await using var registry = new StreamRegistry(options);

            Guid id = Guid.NewGuid();
            registry.GetOrCreateForIngest(id);
            registry.ReleaseIngest(id); // ingest done -> session may be GC'd once idle

            await Task.Delay(120); // exceed idle timeout
            int removed = await registry.SweepIdleAsync();

            Assert.Equal(1, removed);
            Assert.Equal(0, registry.Count);
            Assert.False(registry.TryGet(id, out _));
        }

        [Fact]
        public async Task SweepIdle_KeepsSession_WhileIngestActive()
        {
            var options = new RelaySessionOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) };
            await using var registry = new StreamRegistry(options);

            Guid id = Guid.NewGuid();
            registry.GetOrCreateForIngest(id); // active ingest, never released

            await Task.Delay(120);
            int removed = await registry.SweepIdleAsync();

            Assert.Equal(0, removed);
            Assert.Equal(1, registry.Count);
        }

        [Fact]
        public async Task SweepIdle_KeepsSession_WhileViewersAttached()
        {
            var options = new RelaySessionOptions { IdleTimeout = TimeSpan.FromMilliseconds(50) };
            await using var registry = new StreamRegistry(options);

            Guid id = Guid.NewGuid();
            StreamSession session = registry.GetOrCreateForIngest(id);
            session.AddSubscriber(Guid.NewGuid());
            registry.ReleaseIngest(id); // ingest gone but a viewer remains

            await Task.Delay(120);
            int removed = await registry.SweepIdleAsync();

            Assert.Equal(0, removed); // viewer keeps it alive
            Assert.Equal(1, registry.Count);
        }

        /// <summary>Disposable wrapper so synchronous tests can use a registry without async-using.</summary>
        sealed class RegistryScope : IDisposable
        {
            public StreamRegistry Registry { get; }
            public RegistryScope(RelaySessionOptions options) => Registry = new StreamRegistry(options);
            public void Dispose() => Registry.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
