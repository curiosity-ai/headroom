using Headroom.Abstractions;
using Headroom.Detection;
using Xunit;

namespace Headroom.Tests;

public class ContentDetectorTests
{
    [Fact]
    public void DetectsJsonArrayOfObjects()
    {
        DetectionResult r = ContentDetector.Detect("""[{"id":1},{"id":2}]""");
        Assert.Equal(ContentType.JsonArray, r.ContentType);
        Assert.True((bool)r.Metadata["is_dict_array"]);
        Assert.Equal(1.0, r.Confidence);
    }

    [Fact]
    public void DetectsJsonObject()
    {
        DetectionResult r = ContentDetector.Detect("""{"a":1,"b":2}""");
        Assert.Equal(ContentType.JsonArray, r.ContentType);
        Assert.False((bool)r.Metadata["is_dict_array"]);
    }

    [Fact]
    public void DetectsConcatenatedJsonObjects()
    {
        DetectionResult r = ContentDetector.Detect("""{"title":"a"} {"title":"b"} {"title":"c"}""");
        Assert.Equal(ContentType.JsonArray, r.ContentType);
        Assert.True((bool)r.Metadata["concatenated"]);
    }

    [Fact]
    public void BareScalarIsNotJson()
    {
        DetectionResult r = ContentDetector.Detect("42");
        Assert.Equal(ContentType.PlainText, r.ContentType);
    }

    [Fact]
    public void DetectsGitDiff()
    {
        const string diff = """
            diff --git a/foo.txt b/foo.txt
            --- a/foo.txt
            +++ b/foo.txt
            @@ -1,3 +1,3 @@
            -old line
            +new line
             context
            """;
        DetectionResult r = ContentDetector.Detect(diff);
        Assert.Equal(ContentType.GitDiff, r.ContentType);
    }

    [Fact]
    public void DetectsSearchResults()
    {
        const string search = """
            src/main.py:42:def process():
            src/util.py:10:    return x
            src/util.py:20:    return y
            """;
        DetectionResult r = ContentDetector.Detect(search);
        Assert.Equal(ContentType.SearchResults, r.ContentType);
    }

    [Fact]
    public void DetectsBuildLog()
    {
        const string log = """
            [12:00:00] Starting build
            INFO compiling module a
            WARNING deprecated api used
            ERROR failed to link symbol
            FATAL aborting build
            """;
        DetectionResult r = ContentDetector.Detect(log);
        Assert.Equal(ContentType.BuildOutput, r.ContentType);
    }

    [Fact]
    public void DetectsCSharpSourceCode()
    {
        const string code = """
            using System;
            namespace Foo.Bar;
            public sealed class Widget
            {
                public int Value { get; set; }
            }
            """;
        DetectionResult r = ContentDetector.Detect(code);
        Assert.Equal(ContentType.SourceCode, r.ContentType);
        Assert.Equal("csharp", r.Metadata["language"]);
    }

    [Fact]
    public void DetectsHtml()
    {
        const string html = "<!doctype html><html><head><title>x</title></head><body><div>hi</div></body></html>";
        DetectionResult r = ContentDetector.Detect(html);
        Assert.Equal(ContentType.Html, r.ContentType);
    }

    [Fact]
    public void FallsBackToPlainText()
    {
        DetectionResult r = ContentDetector.Detect("The quick brown fox jumps over the lazy dog.");
        Assert.Equal(ContentType.PlainText, r.ContentType);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyIsPlainTextZeroConfidence(string? input)
    {
        DetectionResult r = ContentDetector.Detect(input);
        Assert.Equal(ContentType.PlainText, r.ContentType);
        Assert.Equal(0.0, r.Confidence);
    }
}
