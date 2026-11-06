using System.Security.Cryptography;
using System.Text;
using PhotoScreener.Core;
using PhotoScreener.Infrastructure;
using PhotoScreener.Services;
using Xunit;

namespace PhotoScreener.Tests;
public class FoundationTests
{
    [Theory]
    [InlineData("DSC_0001.JPG", "abc", "DSC_0001.kAFQ.JPG")]
    [InlineData("Photo.test.png", "message digest", "Photo.test.-Wtp.png")]
    public void LegacyVectors(string name, string bytes, string expected)
    {
        Assert.Equal(expected, LegacyExportName.FromDigest(name, MD5.HashData(Encoding.ASCII.GetBytes(bytes))));
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var source = Path.Combine(dir, name); File.WriteAllText(source, bytes);
            Assert.Equal(expected, Path.GetFileName(PhotoFileService.ComputeTargetPath(source, dir)));
            var target = Path.Combine(dir, "export"); Directory.CreateDirectory(target);
            File.Copy(source, Path.Combine(target, expected));
            Assert.Equal(Path.Combine(target, expected), PhotoFileService.BuildTargetIndex(target)[PhotoFileService.MakePickKey(name, bytes.Length)]);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact] public void NaturalSuffixRetainsBothCandidates()
    {
        Assert.True(LegacyExportName.Candidate("自然.abCD.JPG", "自然.abCD.JPG"));
        Assert.True(LegacyExportName.Candidate("自然.JPG", "自然.abCD.JPG"));
        Assert.Equal("自然.abCD.kAFQ.JPG", LegacyExportName.FromDigest("自然.abCD.JPG", MD5.HashData("abc"u8)));
        var digest = new byte[16]; digest[0] = 255;
        Assert.DoesNotContain('/', LegacyExportName.FromDigest("a.JPG", digest));
    }
    [Fact] public void UnknownParityAndLatestCompletion()
    {
        var state = new PickIntent(); state.Toggle(1); state.Toggle(2); state.Observe(true);
        Assert.True(state.Requested); Assert.False(state.Pending);
        state.Toggle(3); state.Toggle(4); state.Toggle(5);
        state.Complete(true, 4); Assert.False(state.Requested); Assert.True(state.Pending);
        state.Complete(false, 5); Assert.False(state.Pending);
    }
    [Fact] public void FilterFallsForwardThenBackwardThenEmpty()
    {
        Assert.Equal(3, ReviewRules.ChooseAfterFilter(2, 5, i => i == 1 || i == 3));
        Assert.Equal(1, ReviewRules.ChooseAfterFilter(2, 5, i => i == 1));
        Assert.Equal(-1, ReviewRules.ChooseAfterFilter(2, 5, _ => false));
    }
}
