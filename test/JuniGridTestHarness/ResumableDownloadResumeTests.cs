using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using JuniGrid.Services;

namespace JuniGridTestHarness;

public sealed class ResumableDownloadResumeTests
{
    [Fact]
    public async Task PermanentFailureOnFirstCdnFallsThroughToMirror()
    {
        var dir = TempDirs.Create("resume-cdn-fallback");
        var path = Path.Combine(dir, "file.zip");
        var requested = new List<string>();
        using var http = new HttpClient(new StubHandler(request =>
        {
            requested.Add(request.RequestUri!.Host);
            return request.RequestUri.Host == "first.example"
                ? new HttpResponseMessage(HttpStatusCode.Forbidden)
                : new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new ByteArrayContent([7, 8, 9]) };
        }));

        await ResumableDownload.RunAsync(http, "https://first.example/file.zip", path,
            (_, _, _) => { }, fallbackUrls: ["https://mirror.example/file.zip"]);

        Assert.Equal(["first.example", "mirror.example"], requested);
        Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task MidStreamFailureSwitchesCdnAndResumesAtSavedOffset()
    {
        var dir = TempDirs.Create("resume-cdn-midstream");
        var path = Path.Combine(dir, "file.zip");
        var complete = new byte[] { 1, 2, 3, 4, 5, 6 };
        long? resumedOffset = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (request.RequestUri!.Host == "first.example")
            {
                var shortBody = new ByteArrayContent(complete[..3]);
                shortBody.Headers.ContentLength = complete.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = shortBody };
            }

            resumedOffset = request.Headers.Range?.Ranges.Single().From;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                { Content = new ByteArrayContent(complete[3..]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        }));

        await ResumableDownload.RunAsync(http, "https://first.example/file.zip", path,
            (_, _, _) => { }, fallbackUrls: ["https://mirror.example/file.zip"]);

        Assert.Equal(3, resumedOffset);
        Assert.Equal(complete, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task ExistingPartialFileContinuesFromItsExactByteOffset()
    {
        var dir = TempDirs.Create("resume-range");
        var path = Path.Combine(dir, "file.zip");
        var complete = new byte[] { 1, 2, 3, 4, 5, 6 };
        await File.WriteAllBytesAsync(path, complete[..3]);
        long? requestedOffset = null;
        using var http = new HttpClient(new StubHandler(request =>
        {
            requestedOffset = request.Headers.Range?.Ranges.Single().From;
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(complete[3..]),
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        }));

        await ResumableDownload.RunAsync(http, "https://cdn.example/file.zip", path, (_, _, _) => { });

        Assert.Equal(3, requestedOffset);
        Assert.Equal(complete, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task Mismatched416RestartsInsteadOfTreatingPartialFileAsComplete()
    {
        var dir = TempDirs.Create("resume-stale-416");
        var path = Path.Combine(dir, "file.zip");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3 });
        var calls = 0;
        var restartedWithoutRange = false;
        using var http = new HttpClient(new StubHandler(request =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                var rejected = new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
                rejected.Content.Headers.ContentRange = new ContentRangeHeaderValue(8);
                return rejected;
            }
            restartedWithoutRange = request.Headers.Range is null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 }),
            };
        }));

        await ResumableDownload.RunAsync(http, "https://cdn.example/file.zip", path, (_, _, _) => { });

        Assert.Equal(2, calls);
        Assert.True(restartedWithoutRange);
        Assert.Equal(new byte[] { 7, 8, 9, 10, 11, 12, 13, 14 }, await File.ReadAllBytesAsync(path));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
