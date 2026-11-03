using Lycoris.Core.Updates;
using Lycoris.Tests.Updates;

namespace Lycoris.Tests.Robustness;

/// <summary>ReleaseParser.TryParse must never throw — malformed inputs return null + a problem string.</summary>
public sealed class ReleaseInfoFuzzTests
{
    [Fact]
    public void Null_input_returns_null()
    {
        var ex = Record.Exception(() => ReleaseParser.TryParse(null!, out _));
        Assert.Null(ex);
    }

    [Fact]
    public void Empty_string_returns_null()
    {
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse("", out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("{ \"tag")]
    [InlineData("{\"tag_name\":\"v1.0.1\"")]
    [InlineData("\"just a string\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void Non_object_JSON_returns_null(string? snippet)
    {
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(snippet, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Truncated_JSON_array_as_assets_returns_null()
    {
        // Array is opened but never closed.
        var json = "{\"tag_name\":\"v1.0.1\",\"name\":\"Lycoris v1.0.1\",\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1024,\"browser_download_url\":\"https://example.com/Lycoris.exe\"}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Binary_github_fuzzer_strings_do_not_throw()
    {
        // Real oss-fuzz payloads: null bytes, lone surrogates, overlong sequences.
        var payloads = new[]
        {
            "\x00\x01\x02",                          // raw bytes
            "\"\ud800\"",                             // lone surrogate in string
            "{\u0000tag_name:\u0000v1.0.1}",          // null in keys
            "{\"tag_name\":\"\\uD800\\uE000\"}",       // surrogate pair (invalid)
            "\x80\x81\x82\xfe\xff"                    // invalid UTF-8 boundary bytes as string
        };
        foreach (var p in payloads)
        {
            var ex = Record.Exception(() => ReleaseParser.TryParse(p, out _));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void Deep_nesting_does_not_throw()
    {
        // MaxDepth=16 on JsonDocument; 5000 levels should overflow and be caught.
        var depth = 5000;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < depth; i++) sb.Append('{');
        sb.Append("\"tag_name\":\"v1.0.1\",\"assets\":[],\"name\":\"x\",\"html_url\":\"h\",\"body\":\"b\"");
        for (var i = 0; i < depth; i++) sb.Append('}');
        var json = sb.ToString();

        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Large_payload_3MB_does_not_throw()
    {
        // Build a 3 MB string: long tag_name repeated.
        var payload = new char[3 * 1024 * 1024];
        Array.Fill(payload, 'a');
        var json = "{\"tag_name\":\"" + new string(payload) + "\",\"name\":\"Lycoris v999\",\"html_url\":\"https://x\",\"body\":\"\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1,\"browser_download_url\":\"https://x\"}]}";

        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out _));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("v999999999999.1.1")]
    [InlineData("1.0.x")]
    [InlineData("not-a-version")]
    [InlineData("latest")]
    [InlineData("")]
    public void Unparseable_tag_returns_null(string tag)
    {
        // Use Kit.ReleaseJson but override tag_name to force an invalid SemVer tag.
        var json = "{\"tag_name\":\"" + tag + "\",\"name\":\"Lycoris " + tag + "\",\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1024,\"browser_download_url\":\"https://example.com/Lycoris.exe\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Draft_release_returns_null()
    {
        var json = "{\"tag_name\":\"v1.0.1\",\"name\":\"Lycoris v1.0.1\",\"draft\":true,\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1024,\"browser_download_url\":\"https://example.com/Lycoris.exe\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Prerelease_returns_null()
    {
        var json = "{\"tag_name\":\"v1.0.1\",\"name\":\"Lycoris v1.0.1\",\"prerelease\":true,\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1024,\"browser_download_url\":\"https://example.com/Lycoris.exe\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Missing_exe_asset_returns_null()
    {
        var json = "{\"tag_name\":\"v1.0.1\",\"name\":\"Lycoris v1.0.1\",\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Asset_state_not_uploaded_returns_null()
    {
        var json = "{\"tag_name\":\"v1.0.1\",\"name\":\"Lycoris v1.0.1\",\"html_url\":\"https://example.com\",\"body\":\"notes\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"processed\",\"size\":1024,\"browser_download_url\":\"https://example.com/Lycoris.exe\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Version_with_extremely_large_numbers_rejects()
    {
        // tag "v999999999999.1.1" — SemVer.TryParse rejects too-large numeric segments.
        var json = "{\"tag_name\":\"v999999999999.1.1\",\"name\":\"Lycoris v999\",\"html_url\":\"https://x\",\"body\":\"\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1,\"browser_download_url\":\"https://x\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    [Fact]
    public void Version_1_dot_0_dot_x_rejects()
    {
        var json = "{\"tag_name\":\"1.0.x\",\"name\":\"Lycoris 1.0.x\",\"html_url\":\"https://x\",\"body\":\"\",\"assets\":[{\"name\":\"Lycoris.exe\",\"state\":\"uploaded\",\"size\":1,\"browser_download_url\":\"https://x\"}]}";
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.NotNull(problem);
    }

    /// <summary>Positive control: valid input still works.</summary>
    [Fact]
    public void Valid_release_parses_correctly()
    {
        var json = Kit.ReleaseJson();
        string? problem = null;
        var ex = Record.Exception(() => ReleaseParser.TryParse(json, out problem!));
        Assert.Null(ex);
        Assert.Null(problem);
        Assert.NotNull(ReleaseParser.TryParse(json, out _));
    }
}
