using DeskBridge.Core.Updates;

namespace DeskBridge.Core.Tests;

public class ReleaseFeedTests
{
    private const string Api = """
        [
          { "tag_name": "v0.4.0", "draft": false, "prerelease": true,  "body": "beta", "assets": [] },
          { "tag_name": "v0.3.1", "draft": false, "prerelease": false, "body": "<!-- deskbridge-notes -->\n## What's new\n\n- **Fixed** the `agent`\n- Faster\n<!-- /deskbridge-notes -->\n\n## Changelog\n- abc",
            "assets": [ { "name": "DeskBridge-0.3.1-win-x64.exe", "browser_download_url": "https://x/exe", "size": 123 },
                        { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/sums", "size": 99 } ] },
          { "tag_name": "v0.5.0", "draft": true,  "prerelease": false, "body": "", "assets": [] },
          { "tag_name": "nightly", "draft": false, "prerelease": true, "body": "", "assets": [] },
          { "tag_name": "v0.3.0-rc1", "draft": false, "prerelease": true, "body": "", "assets": [] },
          { "tag_name": "v0.2.0", "draft": false, "prerelease": false, "body": "", "assets": [] }
        ]
        """;

    [Fact]
    public void Parse_skips_drafts_and_tags_that_are_not_plain_versions()
    {
        var releases = ReleaseFeed.Parse(Api);

        Assert.Equal(["v0.4.0", "v0.3.1", "v0.2.0"], releases.Select(r => r.Tag));
        var r = releases[1];
        Assert.Equal(new SemVersion(0, 3, 1), r.Version);
        Assert.False(r.Prerelease);
        Assert.Equal(123, r.Asset("DeskBridge-0.3.1-win-x64.exe")!.Size);
        Assert.Equal("https://x/sums", r.Asset(ReleaseFeed.ChecksumsAsset)!.DownloadUrl);
    }

    [Fact]
    public void Stable_channel_ignores_betas_and_beta_channel_takes_the_newest()
    {
        var releases = ReleaseFeed.Parse(Api);
        var current = new SemVersion(0, 2, 0);

        Assert.Equal(["v0.3.1"], ReleaseFeed.Newer(releases, current, betas: false).Select(r => r.Tag));
        Assert.Equal(["v0.4.0", "v0.3.1"], ReleaseFeed.Newer(releases, current, betas: true).Select(r => r.Tag));
        Assert.Empty(ReleaseFeed.Newer(releases, new SemVersion(0, 4, 0), betas: true));
    }

    [Fact]
    public void Notes_come_from_the_marked_block_without_markdown()
    {
        var body = ReleaseFeed.Parse(Api)[1].Body;

        Assert.Equal(["Fixed the agent", "Faster"], ReleaseFeed.NoteLines(ReleaseFeed.BodyBlock(body)));
        Assert.Equal(["abc"], ReleaseFeed.NoteLines(ReleaseFeed.BodyBlock("## Changelog\n- abc")));
        Assert.Equal(["plain line"], ReleaseFeed.NoteLines("# Title\nplain line"));
        Assert.Empty(ReleaseFeed.NoteLines(ReleaseFeed.BodyBlock("nothing marked")));
    }

    [Fact]
    public void Versions_compare_numerically()
    {
        Assert.True(new SemVersion(0, 10, 0) > new SemVersion(0, 9, 9));
        Assert.Equal(new SemVersion(1, 2, 3), SemVersion.TryParse("v1.2.3"));
        Assert.Equal(new SemVersion(1, 2, 3), SemVersion.TryParse("1.2.3+abcdef"));
        Assert.Null(SemVersion.TryParse("1.2"));
        Assert.Equal("docs/release-notes/v1.2.3.md", ReleaseFeed.NotesFile(new(1, 2, 3), "en"));
        Assert.Equal("docs/release-notes/v1.2.3.es.md", ReleaseFeed.NotesFile(new(1, 2, 3), "es"));
    }

    [Fact]
    public void Checksums_are_read_from_sha256sum_output()
    {
        var hash = new string('a', 64);
        var sums = $"{new string('b', 64)}  other.exe\n{hash} *DeskBridge-1.0.0-win-x64.exe\r\n";

        Assert.Equal(hash, ReleaseFeed.ExpectedSha256(sums, "DeskBridge-1.0.0-win-x64.exe"));
        Assert.Null(ReleaseFeed.ExpectedSha256(sums, "missing.exe"));
        Assert.Null(ReleaseFeed.ExpectedSha256("nothex  DeskBridge-1.0.0-win-x64.exe", "DeskBridge-1.0.0-win-x64.exe"));
    }
}
