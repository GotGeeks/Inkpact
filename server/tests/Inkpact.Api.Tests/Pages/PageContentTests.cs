using Inkpact.Api.Pages;
using Inkpact.Api.Sync;

namespace Inkpact.Api.Tests.Pages;

/// <summary>The content of a page made in one step (a copy of offline edits), and the limits on it. Run 1.21.</summary>
public class PageContentTests
{
    private static TreeError? Read(int bytes, long max, out byte[]? content) =>
        PageEndpoints.ReadContent(Convert.ToBase64String(new byte[bytes]), max, out content);

    [Fact]
    public void Content_up_to_the_limit_is_decoded()
    {
        Read(10, max: 10, out var content).ShouldBeNull();

        content.ShouldNotBeNull().Length.ShouldBe(10);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void One_or_two_bytes_over_a_limit_that_base64_rounds_up_are_refused_after_decoding(int bytes)
    {
        // 10, 11 and 12 bytes are all 16 characters of base64, so the length of the text cannot tell them apart.
        Convert.ToBase64String(new byte[bytes]).Length.ShouldBe(Convert.ToBase64String(new byte[10]).Length);

        Read(bytes, max: 10, out var content).ShouldBe(new TreeError(413, "content_too_large", "content"));
        content.ShouldBeNull();
    }

    [Fact]
    public void Content_whose_text_is_already_too_long_is_refused_without_decoding()
    {
        PageEndpoints.ReadContent(new string('!', 20), max: 10, out var content)
            .ShouldBe(new TreeError(413, "content_too_large", "content"));
        content.ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    public void Content_that_is_empty_or_not_base64_is_invalid(string encoded)
    {
        PageEndpoints.ReadContent(encoded, max: 10, out var content).ShouldBe(new TreeError(400, "content_invalid", "content"));
        content.ShouldBeNull();
    }

    [Fact]
    public void A_page_is_half_the_queue_and_a_copy_an_eighth_more_with_the_defaults()
    {
        var sync = new SyncOptions();

        sync.MaxPageBytes.ShouldBe(8L * 1024 * 1024);
        sync.MaxCopyBytes.ShouldBe(9L * 1024 * 1024);
    }

    [Fact]
    public void A_small_queue_makes_the_page_smaller_than_a_sync_message_so_that_it_can_still_be_opened()
    {
        var sync = new SyncOptions { MaxQueuedBytes = 2 * 1024 * 1024, MaxMessageBytes = 2 * 1024 * 1024 };

        sync.MaxPageBytes.ShouldBe(1024 * 1024);
        sync.MaxCopyBytes.ShouldBeLessThan(sync.MaxQueuedBytes);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(9L * 1024 * 1024)]
    [InlineData(9L * 1024 * 1024 + 1)]
    public void The_route_takes_a_copy_in_base64_and_the_rest_of_the_request(long maxCopy)
    {
        // A queue whose copy limit is maxCopy, from MaxCopyBytes = q/2 + q/16.
        var sync = new SyncOptions { MaxQueuedBytes = maxCopy * 16 / 9 };
        while (sync.MaxCopyBytes < maxCopy) sync.MaxQueuedBytes++;

        PageEndpoints.MaxCreateBodyBytes(sync).ShouldBe(Convert.ToBase64String(new byte[sync.MaxCopyBytes]).Length + PageEndpoints.RequestOverhead);
    }
}
