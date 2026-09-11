using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Themia.Imaging.Tests.Fixtures;
using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// The four properties of <see cref="ImageProcessingOptions.MaxConcurrency"/> from coord #0125.
/// <see cref="ImageProcessingOptions.MaxPixels"/> bounds one decode; without a bound on how many run at
/// once the real ceiling is <c>MaxPixels × 4 bytes × concurrent calls</c> and the package supplied only
/// the first two terms.
/// </summary>
/// <remarks>
/// Every test here is gated on the semaphore's own state rather than on elapsed time: the test occupies
/// slots itself and asserts on <see cref="System.Threading.SemaphoreSlim.CurrentCount"/> and on whether a
/// task can possibly have completed. While the test holds the only slot a queued call <i>cannot</i>
/// finish, so <c>IsCompleted == false</c> is a fact rather than a race. The timeouts below are hang
/// guards for the falsification runs, never the assertion — a correct implementation completes these
/// immediately and nothing here sleeps.
/// </remarks>
public sealed class ConcurrencyBoundTests
{
    /// <summary>Bounds a genuine hang (a falsification run) without ever being what a passing test waits on.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Fact]
    public void MaxConcurrency_defaults_to_the_same_small_bound_Themia_Pdf_ships()
    {
        // Deliberately not "unbounded". An unbounded default makes worst-case decode memory a function of
        // inbound traffic — a browser uploading eight selected photos in parallel is eight requests.
        Assert.Equal(2, new ImageProcessingOptions().MaxConcurrency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_bound_below_one_fails_the_host_at_startup_and_names_the_value(int maxConcurrency)
    {
        // Alongside MaxEdge/MaxPixels/Quality in Validate(), so ValidateOnStart refuses it at boot. Zero
        // in particular would not throttle, it would deadlock: every caller queues on a gate nothing opens.
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddThemiaImaging(o => o.MaxConcurrency = maxConcurrency);

        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("MaxConcurrency", string.Join(" ", error.Failures), StringComparison.Ordinal);
        Assert.Contains(maxConcurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(" ", error.Failures), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_bound_below_one_is_refused_by_the_constructor_too(int maxConcurrency)
    {
        // The type is constructible directly, including by a test, and a semaphore's capacity is fixed at
        // construction — so an invalid bound has to fail here rather than at the first upload.
        var error = Assert.Throws<ArgumentException>(() => TestProcessor.Build(o => o.MaxConcurrency = maxConcurrency));

        Assert.Contains("MaxConcurrency", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void The_gate_admits_exactly_MaxConcurrency_callers(int maxConcurrency)
    {
        // The capacity comes from the option rather than a constant — the thing that makes this a knob
        // rather than a hard-coded 2.
        var processor = TestProcessor.Build(o => o.MaxConcurrency = maxConcurrency);

        Assert.Equal(maxConcurrency, processor.Slots.CurrentCount);
        for (var i = 0; i < maxConcurrency; i++)
        {
            Assert.True(processor.Slots.Wait(0), $"slot {i + 1} of {maxConcurrency} should have been free");
        }

        Assert.False(processor.Slots.Wait(0), $"a {maxConcurrency + 1}th caller must queue, not proceed");
    }

    // ---- Property 1: a waiter honours its cancellation token -------------------------------------

    [Fact]
    public async Task A_caller_queued_behind_the_bound_honours_its_cancellation_token()
    {
        // A client that disconnects while queued must free its slot instead of waiting for one it will
        // not use. The token is cancelled only AFTER the source stream reports end-of-stream, so
        // CopyToAsync cannot be what observes it: past that point the gate is the only remaining check,
        // whether the call is already parked there or arrives to find the token already cancelled.
        var processor = TestProcessor.Build(o => o.MaxConcurrency = 1);
        Assert.True(processor.Slots.Wait(0));

        using var cts = new CancellationTokenSource();
        await using var source = new DrainSignallingStream(TestImages.Jpeg(400, 300));
        var queued = processor.ProcessAsync(source, cancellationToken: cts.Token);

        await source.Drained.WaitAsync(HangGuard);
        Assert.False(queued.IsCompleted, "it cannot have finished — the test holds the only slot");

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(HangGuard));

        // And it left the gate as it found it: releasing the test's own slot restores the full bound, so
        // the cancelled caller was never charged one.
        processor.Slots.Release();
        Assert.Equal(1, processor.Slots.CurrentCount);
    }

    // ---- Property 2: the slot is released on the throwing paths too -----------------------------

    [Fact]
    public async Task A_throw_from_inside_the_gate_gives_the_slot_back()
    {
        // WebP cannot encode an edge longer than 16383px, so a 16500px panorama decodes fine and then
        // fails at the encode — a throw raised while the caller holds a slot. On an upload endpoint a
        // throw is ordinary user input, not the rare case, so a release that only happened on success
        // would turn the first bad upload into a permanently lost slot.
        var processor = TestProcessor.Build(o =>
        {
            o.MaxConcurrency = 1;
            o.MaxEdge = 20_000;
            o.MaxPixels = 1_000_000_000;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(new MemoryStream(TestImages.Png(16_500, 8))));

        Assert.Equal(1, processor.Slots.CurrentCount);
    }

    [Fact]
    public async Task A_first_upload_that_throws_does_not_lock_out_the_next_one()
    {
        // The same property stated the way a user meets it. With a bound of 1 and no release on the
        // throwing path, the very first bad upload leaks the only slot and every later caller queues
        // forever; this call would never return.
        var processor = TestProcessor.Build(o =>
        {
            o.MaxConcurrency = 1;
            o.MaxEdge = 20_000;
            o.MaxPixels = 1_000_000_000;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(new MemoryStream(TestImages.Png(16_500, 8))));

        using var result = await processor
            .ProcessAsync(new MemoryStream(TestImages.Jpeg(800, 600)))
            .WaitAsync(HangGuard);

        Assert.Equal(800, result.Width);
    }

    [Fact]
    public async Task An_over_budget_refusal_does_not_cost_a_slot_either()
    {
        // The other throwing path the reporter named. It is refused before the gate (see below), so the
        // slot is never taken rather than taken and returned — the observable outcome a caller cares
        // about is the same, and it is what this asserts.
        var processor = TestProcessor.Build(o => o.MaxConcurrency = 1);

        await Assert.ThrowsAsync<ArgumentException>(
            () => processor.ProcessAsync(new MemoryStream(TestImages.HeaderOnlyPng(12_000, 12_000))));

        Assert.Equal(1, processor.Slots.CurrentCount);

        using var result = await processor
            .ProcessAsync(new MemoryStream(TestImages.Jpeg(800, 600)))
            .WaitAsync(HangGuard);

        Assert.Equal(800, result.Width);
    }

    // ---- Property 3: the pixel budget is checked before the slot is taken ------------------------

    [Fact]
    public async Task An_over_budget_image_is_refused_without_waiting_for_a_slot()
    {
        // Ordering, asserted rather than assumed: with every slot occupied the refusal still returns.
        // Gate first and this call would park behind a decode it is never going to perform, letting a
        // 48-byte bomb queue in front of legitimate callers instead of merely being rejected.
        var processor = TestProcessor.Build(o => o.MaxConcurrency = 1);
        Assert.True(processor.Slots.Wait(0));

        var refusal = processor.ProcessAsync(new MemoryStream(TestImages.HeaderOnlyPng(12_000, 12_000)));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => refusal.WaitAsync(HangGuard));

        Assert.Contains("12000x12000", error.Message, StringComparison.Ordinal);

        // It never touched the gate: the test's slot is still the only one out.
        Assert.Equal(0, processor.Slots.CurrentCount);
        processor.Slots.Release();
        Assert.Equal(1, processor.Slots.CurrentCount);
    }

    // ---- Property 4: the limit is per registration, not per instance -----------------------------

    [Fact]
    public async Task The_bound_is_shared_by_every_resolution_of_one_registration()
    {
        // AddThemiaImaging registers the processor as a singleton, so an instance field is per-
        // registration by construction. Asserted anyway: that is a property of the registration, which
        // can change, not of the class. A per-request instance would hand each caller its own gate and
        // bound nothing at all.
        var services = new ServiceCollection();
        services.AddThemiaImaging(o => o.MaxConcurrency = 1);
        using var provider = services.BuildServiceProvider();

        var first = Assert.IsType<SkiaImageProcessor>(provider.GetRequiredService<IImageProcessor>());
        var second = Assert.IsType<SkiaImageProcessor>(provider.GetRequiredService<IImageProcessor>());

        Assert.True(first.Slots.Wait(0));

        // The second resolution sees the slot the first one took — one gate, not two.
        Assert.Equal(0, second.Slots.CurrentCount);

        var queued = second.ProcessAsync(new MemoryStream(TestImages.Jpeg(800, 600)));
        Assert.False(queued.IsCompleted, "it cannot have finished — the only slot is held");

        first.Slots.Release();

        using var result = await queued.WaitAsync(HangGuard);
        Assert.Equal(800, result.Width);
    }

    [Fact]
    public async Task Every_caller_of_one_processor_queues_on_the_same_gate()
    {
        // Three real calls, none of which can proceed while the test holds the only slot, and all of
        // which complete once it is released. No timing assertion can be read into this: the "before"
        // state is impossible to reach by being slow.
        var processor = TestProcessor.Build(o => o.MaxConcurrency = 1);
        Assert.True(processor.Slots.Wait(0));

        var calls = Enumerable.Range(0, 3)
            .Select(_ => processor.ProcessAsync(new MemoryStream(TestImages.Jpeg(800, 600))))
            .ToArray();

        Assert.All(calls, c => Assert.False(c.IsCompleted, "no call can finish while the only slot is held"));

        processor.Slots.Release();

        var results = await Task.WhenAll(calls).WaitAsync(HangGuard);
        try
        {
            Assert.All(results, r => Assert.Equal(800, r.Width));
        }
        finally
        {
            foreach (var result in results)
            {
                result.Dispose();
            }
        }
    }

    /// <summary>
    /// A stream whose end-of-read is observable, so a test can cancel at a point where the concurrency
    /// gate is provably the only thing left that inspects the token.
    /// </summary>
    private sealed class DrainSignallingStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the processor has read this stream to its end.</summary>
        internal Task Drained => drained.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Signal(inner.Read(buffer, offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Signal(inner.Read(buffer.Span)));
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Signal(inner.Read(buffer, offset, count)));
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                drained.TrySetResult();
            }

            base.Dispose(disposing);
        }

        private int Signal(int read)
        {
            if (read == 0)
            {
                drained.TrySetResult();
            }

            return read;
        }
    }
}
