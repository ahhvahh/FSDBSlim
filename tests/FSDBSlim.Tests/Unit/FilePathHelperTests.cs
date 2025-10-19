namespace FSDBSlim.Tests.Unit;

using System;
using FSDBSlim.Utils;
using FluentAssertions;
using Xunit;

public class FilePathHelperTests
{
    [Theory]
    [InlineData("folder/sub/file.txt", "folder/sub/file.txt")]
    [InlineData("/folder//sub/./file.txt", "folder/sub/file.txt")]
    [InlineData("folder\\sub\\file.txt", "folder/sub/file.txt")]
    public void Normalize_should_clean_path(string input, string expected)
    {
        FilePathHelper.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("a/b/file.txt", "file.txt", "txt")]
    [InlineData("file", "file", null)]
    [InlineData("folder/file.tar.gz", "file.tar.gz", "gz")]
    public void FileName_and_extension_should_be_derived(string normalized, string name, string? extension)
    {
        FilePathHelper.GetFileName(normalized).Should().Be(name);
        FilePathHelper.GetFileNameAndExtension(normalized).Should().Be((name, extension));
    }

    [Fact]
    public void Normalize_should_reject_parent_segments()
    {
        Action act = () => FilePathHelper.Normalize("../../etc/passwd");
        act.Should().Throw<InvalidOperationException>();
    }
}
