using System.Security.Cryptography;
using System.Text;
using Inkpact.Api.Attachments;

namespace Inkpact.Api.Tests.Attachments;

public sealed class LocalAttachmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"inkpact-store-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private LocalAttachmentStore CreateStore() => new(new AttachmentOptions { Directory = _root });

    private string Temporary => Path.Combine(_root, "tmp");

    private static string Sha256Of(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.Shared.NextBytes(bytes);
        return bytes;
    }

    /// <summary>Every stored file under the root, without the temporary folder.</summary>
    private string[] StoredFiles() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Where(f => !f.StartsWith(Temporary)).ToArray();

    [Fact]
    public async Task A_file_is_stored_under_its_hash_and_reads_back_the_same()
    {
        var store = CreateStore();
        var content = RandomBytes(300_000); // more than one buffer

        var stored = await store.PutAsync(new MemoryStream(content), maxSize: 1_000_000);

        stored.ShouldBe(new StoredFile(Sha256Of(content), content.Length));
        store.PathFor(stored.Sha256).ShouldBe(Path.Combine(Path.GetFullPath(_root), stored.Sha256[..2], stored.Sha256[2..4], stored.Sha256));
        await using var read = (await store.OpenReadAsync(stored.Sha256))!;
        var back = new MemoryStream();
        await read.CopyToAsync(back);
        back.ToArray().ShouldBe(content);
    }

    [Fact]
    public async Task The_same_content_twice_is_one_file()
    {
        var store = CreateStore();
        var content = Encoding.UTF8.GetBytes("the same picture");

        var first = await store.PutAsync(new MemoryStream(content), 1000);
        var second = await store.PutAsync(new MemoryStream(content), 1000);

        second.ShouldBe(first);
        StoredFiles().ShouldHaveSingleItem();
        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
    }

    [Fact]
    public async Task Uploads_of_the_same_content_at_the_same_time_all_succeed()
    {
        var store = CreateStore();
        var content = RandomBytes(200_000);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.PutAsync(new MemoryStream(content), 1_000_000))));

        results.Distinct().ShouldHaveSingleItem().Sha256.ShouldBe(Sha256Of(content));
        StoredFiles().ShouldHaveSingleItem();
        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_file_at_the_limit_is_stored_and_one_byte_more_is_refused_and_leaves_nothing()
    {
        var store = CreateStore();

        (await store.PutAsync(new MemoryStream(RandomBytes(1000)), maxSize: 1000)).Size.ShouldBe(1000);
        var refused = await Should.ThrowAsync<FileTooLargeException>(() => store.PutAsync(new MemoryStream(RandomBytes(1001)), maxSize: 1000));

        refused.MaxSize.ShouldBe(1000);
        StoredFiles().ShouldHaveSingleItem();
        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failing_or_cancelled_upload_leaves_nothing()
    {
        var store = CreateStore();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Should.ThrowAsync<IOException>(() => store.PutAsync(new BrokenStream(), 1_000_000));
        await Should.ThrowAsync<OperationCanceledException>(() => store.PutAsync(new MemoryStream(RandomBytes(10)), 1000, cancelled.Token));

        StoredFiles().ShouldBeEmpty();
        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
    }

    [Fact]
    public async Task Starting_removes_what_an_interrupted_upload_left_and_keeps_the_files()
    {
        var stored = await CreateStore().PutAsync(new MemoryStream(RandomBytes(10)), 1000);
        File.WriteAllText(Path.Combine(Temporary, "cut-off-by-a-restart"), "half a file");

        var store = CreateStore();

        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
        (await store.OpenReadAsync(stored.Sha256)).ShouldNotBeNull().Dispose();
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")] // well formed, not there
    [InlineData("../../../../etc/passwd")]
    [InlineData("ABCDEF0000000000000000000000000000000000000000000000000000000000")] // upper case is not how they are written
    [InlineData("abc")]
    [InlineData("")]
    public async Task Anything_but_the_hash_of_a_stored_file_opens_nothing(string sha256)
    {
        (await CreateStore().OpenReadAsync(sha256)).ShouldBeNull();
    }

    [Fact]
    public async Task An_empty_file_is_hashed_but_not_kept()
    {
        // Refusing it is the service's decision; nothing can refer to it, so the store keeps nothing.
        var stored = await CreateStore().PutAsync(new MemoryStream(), 1000);

        stored.ShouldBe(new StoredFile(Sha256Of([]), 0));
        StoredFiles().ShouldBeEmpty();
        Directory.EnumerateFiles(Temporary).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(300_000)]
    public async Task The_first_bytes_come_back_with_the_hash(int length)
    {
        var content = RandomBytes(length);

        var stored = await CreateStore().PutAsync(new TrickleStream(content), 1_000_000);

        stored.Head.ShouldBe(content[..Math.Min(length, StoredFile.HeadLength)]);
    }

    [Fact]
    public async Task Stored_files_and_folders_are_private_to_the_app_user()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Unix file modes only
        }

        var store = CreateStore();
        var stored = await store.PutAsync(new MemoryStream(RandomBytes(10)), 1000);

        File.GetUnixFileMode(store.PathFor(stored.Sha256)).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.GetUnixFileMode(_root).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Hands out three bytes per read, as a slow connection might, so the head is gathered over several reads.</summary>
    private sealed class TrickleStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], cancellationToken);
    }

    private sealed class BrokenStream : Stream
    {
        private int _reads;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) =>
            _reads++ == 0 ? count : throw new IOException("the connection was lost");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            _reads++ == 0 ? ValueTask.FromResult(buffer.Length) : throw new IOException("the connection was lost");

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
