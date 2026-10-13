using System.Net;
using WadSmith.Core.Games;

namespace WadSmith.Tests;

/// <summary>A game's cover picture (D53): fetched once from its article and kept; none remembered; a failure tried again.</summary>
public sealed class CoverArtTests
{
    private sealed class Fake(Func<Uri, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request.RequestUri!));
        }
    }

    private static readonly byte[] Picture = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task TheCoverIsFetchedOnceAndKept()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "covers");
        var game = GameCatalog.Get("doom2")!;
        Assert.Equal("Doom II", game.Wiki);
        var fake = new Fake(uri => uri.ToString() == CoverArt.SummaryApi + "Doom_II"
            ? Json("""{"title":"Doom II","thumbnail":{"source":"https://upload.wikimedia.org/x/320px-Doom_II_cover.jpg","width":320,"height":463}}""")
            : uri.ToString().EndsWith("Doom_II_cover.jpg", StringComparison.Ordinal) ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Picture) } : new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(fake);

        var file = await CoverArt.FetchAsync(game, folder, http, TestContext.Current.CancellationToken);
        Assert.Equal(Path.Combine(folder, "Doom_II.jpg"), file);
        Assert.Equal(Picture, await File.ReadAllBytesAsync(file!, TestContext.Current.CancellationToken));
        Assert.Equal([CoverArt.SummaryApi + "Doom_II", "https://upload.wikimedia.org/x/320px-Doom_II_cover.jpg"], fake.Asked);

        // Kept: the second time nothing is asked for, and the cached picture is found without a fetch.
        Assert.Equal(file, await CoverArt.FetchAsync(game, folder, http, TestContext.Current.CancellationToken));
        Assert.Equal(2, fake.Asked.Count);
        Assert.Equal(file, CoverArt.Cached(game, folder));
    }

    [Fact]
    public async Task NoPictureIsRememberedAFailureIsTriedAgainAndAGameWithoutAnArticleAsksNothing()
    {
        using var temp = new TempFolder();
        var game = GameCatalog.Get("doom2")!;

        // An article without a picture: remembered, so the next start asks nothing.
        var bare = new Fake(_ => Json("""{"title":"Doom II"}"""));
        using var bareHttp = new HttpClient(bare);
        var folder = Path.Combine(temp.Path, "bare");
        Assert.Null(await CoverArt.FetchAsync(game, folder, bareHttp, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(folder, "Doom_II.none")));
        Assert.Null(await CoverArt.FetchAsync(game, folder, bareHttp, TestContext.Current.CancellationToken));
        Assert.Single(bare.Asked);

        // A failed fetch: nothing kept, asked again next time.
        var down = new Fake(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var downHttp = new HttpClient(down);
        var other = Path.Combine(temp.Path, "down");
        Assert.Null(await CoverArt.FetchAsync(game, other, downHttp, TestContext.Current.CancellationToken));
        Assert.Null(await CoverArt.FetchAsync(game, other, downHttp, TestContext.Current.CancellationToken));
        Assert.Equal(2, down.Asked.Count);
        Assert.False(Directory.Exists(other) && Directory.EnumerateFiles(other).Any());

        // A game without an article: nothing asked.
        var none = GameCatalog.Get("blasphemer")!;
        Assert.Null(none.Wiki);
        Assert.Null(await CoverArt.FetchAsync(none, other, downHttp, TestContext.Current.CancellationToken));
        Assert.Equal(2, down.Asked.Count);
    }
}
