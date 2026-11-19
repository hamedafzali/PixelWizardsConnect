using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PixelWizard.Signaling;
using Xunit;

namespace PixelWizard.Tests;

/// <summary>
/// The shared contract with the router: every file in router-server/testdata/signaling parses into
/// its typed message and re-serializes to the same JSON value (not necessarily the same bytes).
/// </summary>
public class SignalingGoldenTests
{
    private static string[] Files() =>
        Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "signaling"), "*.json");

    // The repo copy, when the tests run from a checkout: the linked set must be all of it.
    private static string? SourceDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "router-server", "testdata", "signaling");
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    [Fact]
    public void AllGoldenFilesAreLinkedIntoTheTestOutput()
    {
        var files = Files();
        Assert.NotEmpty(files);
        if (SourceDir() is string src)
            Assert.Equal(Directory.GetFiles(src, "*.json").Length, files.Length);
    }

    [Fact]
    public void EveryGoldenFile_ParsesAndReserializesToTheSameJsonValue()
    {
        foreach (string file in Files())
        {
            string json = File.ReadAllText(file);
            var msg = SignalingJson.Parse(json);
            Assert.True(msg != null, $"{Path.GetFileName(file)} did not parse");
            Assert.Equal(Path.GetFileNameWithoutExtension(file), msg!.Type);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(SignalingJson.Serialize(msg))),
                $"{Path.GetFileName(file)} changed on re-serialization: {SignalingJson.Serialize(msg)}");
        }
    }
}
