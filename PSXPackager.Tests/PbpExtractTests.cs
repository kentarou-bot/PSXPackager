using System.Security.Cryptography;
using System.Text;
using Popstation;
using Popstation.Pbp;
using PbpExtract;

namespace UnitTestProject;

[TestClass]
public class PbpExtractTests
{
    private const int SectorSize = 2352;
    private const int DataSectors = 160;
    private const int AudioSectors = 160;

    [TestMethod]
    public void CompressedRoundTripPreservesBytesPastTheIsoVolume()
    {
        RoundTrip(compressionLevel: 5);
    }

    [TestMethod]
    public void StoredRoundTripPreservesBytesPastTheIsoVolume()
    {
        RoundTrip(compressionLevel: 0);
    }

    [TestMethod]
    public void ProtectedPsarIsRefusedAndLeavesNoOutput()
    {
        using var workspace = new Workspace();
        var image = BuildImage();
        var pbp = workspace.Pack(image, compressionLevel: 5);
        var source = File.ReadAllBytes(pbp);
        var hash = Convert.ToHexString(SHA256.HashData(source));
        var protectedCopy = Path.Combine(workspace.Root, "protected.pbp");
        File.WriteAllBytes(protectedCopy, source);
        StampPgd(protectedCopy);

        var output = Path.Combine(workspace.Root, "out");
        var ex = Assert.ThrowsException<PbpFormatException>(() =>
            new PbpImageExtractor().Extract(protectedCopy, output, overwrite: false, discs: null));

        Assert.IsTrue(ex.IsProtected);
        Assert.IsFalse(Directory.Exists(output) && Directory.EnumerateFiles(output).Any());
        CollectionAssert.AreEqual(source, File.ReadAllBytes(pbp));
        Assert.AreEqual(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pbp))));
    }

    [TestMethod]
    public void CorruptDeflateBlockIsRefusedWithoutAPartialBin()
    {
        using var workspace = new Workspace();
        var image = BuildImage();
        var pbp = workspace.Pack(image, compressionLevel: 5);
        CorruptFirstBlock(pbp);

        var output = Path.Combine(workspace.Root, "out");
        Assert.ThrowsException<PbpFormatException>(() =>
            new PbpImageExtractor().Extract(pbp, output, overwrite: false, discs: null));

        Assert.IsFalse(File.Exists(Path.Combine(output, "disc.bin")));
        Assert.IsFalse(File.Exists(Path.Combine(output, "disc.bin.partial")));
    }

    [TestMethod]
    public void ExistingOutputIsLeftAloneUnlessOverwriteIsSet()
    {
        using var workspace = new Workspace();
        var image = BuildImage();
        var pbp = workspace.Pack(image, compressionLevel: 0);
        var output = Path.Combine(workspace.Root, "out");
        new PbpImageExtractor().Extract(pbp, output, overwrite: false, discs: null);

        var bin = Path.Combine(output, "disc.bin");
        File.WriteAllText(bin, "keep");

        Assert.ThrowsException<IOException>(() =>
            new PbpImageExtractor().Extract(pbp, output, overwrite: false, discs: null));
        Assert.AreEqual("keep", File.ReadAllText(bin));

        new PbpImageExtractor().Extract(pbp, output, overwrite: true, discs: null);
        Assert.AreEqual(image.Length, new FileInfo(bin).Length);
    }

    [TestMethod]
    public void PublishedDllExtractsWhenTheArtifactPathIsSupplied()
    {
        var dll = Environment.GetEnvironmentVariable("PBP_EXTRACT_DLL");
        if (string.IsNullOrWhiteSpace(dll))
        {
            Assert.Inconclusive("PBP_EXTRACT_DLL is not set. The in-process tests still cover extraction.");
        }

        using var workspace = new Workspace();
        var image = BuildImage();
        var pbp = workspace.Pack(image, compressionLevel: 5);
        var before = File.ReadAllBytes(pbp);
        var output = Path.Combine(workspace.Root, "cli-out");

        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{dll}\" --input \"{pbp}\" --output \"{output}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        using var process = System.Diagnostics.Process.Start(start);
        Assert.IsNotNull(process);
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.AreEqual(0, process.ExitCode, stdout + stderr);
        CollectionAssert.AreEqual(before, File.ReadAllBytes(pbp));
        CollectionAssert.AreEqual(image, File.ReadAllBytes(Path.Combine(output, "disc.bin")));
        StringAssert.Contains(stdout, "source-unchanged: True");
    }

    [TestMethod]
    public void FileThatIsNotAPbpIsRefused()
    {
        using var workspace = new Workspace();
        var path = Path.Combine(workspace.Root, "not.pbp");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        var ex = Assert.ThrowsException<PbpFormatException>(() =>
            new PbpImageExtractor().Extract(path, Path.Combine(workspace.Root, "out"), false, null));

        Assert.IsFalse(ex.IsProtected);
    }

    private static void RoundTrip(int compressionLevel)
    {
        using var workspace = new Workspace();
        var image = BuildImage();
        var pbp = workspace.Pack(image, compressionLevel);
        var before = File.ReadAllBytes(pbp);
        var writeTime = File.GetLastWriteTimeUtc(pbp);

        var output = Path.Combine(workspace.Root, "out-" + compressionLevel);
        var report = new PbpImageExtractor().Extract(pbp, output, overwrite: false, discs: null);

        CollectionAssert.AreEqual(before, File.ReadAllBytes(pbp));
        Assert.AreEqual(writeTime, File.GetLastWriteTimeUtc(pbp));
        Assert.IsTrue(report.SourceUnchanged);
        Assert.AreEqual(before.Length, report.SourceLength);

        var disc = report.Discs.Single();
        var extracted = File.ReadAllBytes(disc.BinPath);
        CollectionAssert.AreEqual(image, extracted);
        Assert.AreEqual(image.Length, disc.BinLength);
        Assert.AreEqual("toc-lead-out", disc.LengthSource);
        Assert.AreEqual((long)DataSectors * SectorSize, disc.Iso9660Bytes);
        Assert.IsTrue(disc.BinLength > disc.Iso9660Bytes);
        Assert.AreEqual(Hash(image, MD5.Create()), disc.Md5);
        Assert.AreEqual(Hash(image, SHA256.Create()), disc.Sha256);

        var data = disc.Tracks.Single(track => track.Number == 1);
        var audio = disc.Tracks.Single(track => track.Number == 2);
        Assert.AreEqual(0, data.Offset);
        Assert.AreEqual((long)DataSectors * SectorSize, data.Length);
        Assert.AreEqual((long)DataSectors * SectorSize, audio.Offset);
        Assert.AreEqual((long)AudioSectors * SectorSize, audio.Length);
        Assert.AreEqual(Hash(image.AsSpan(0, (int)data.Length).ToArray(), MD5.Create()), data.Md5);
        Assert.AreEqual("audio", audio.Type);
        Assert.IsTrue(disc.UsedStandardAudioPregap);

        var cue = File.ReadAllText(disc.CuePath);
        StringAssert.Contains(cue, "TRACK 01 MODE2/2352");
        StringAssert.Contains(cue, "TRACK 02 AUDIO");
        StringAssert.Contains(File.ReadAllText(disc.ReportPath), "md5: " + disc.Md5);
    }

    private static byte[] BuildImage()
    {
        var total = DataSectors + AudioSectors;
        var image = new byte[total * SectorSize];
        var random = new Random(12345);
        for (var sector = 0; sector < total; sector++)
        {
            var offset = sector * SectorSize;
            image[offset] = 0x00;
            for (var i = 1; i <= 10; i++)
            {
                image[offset + i] = 0xFF;
            }

            image[offset + 11] = 0x00;
            image[offset + 15] = 0x02;
            random.NextBytes(image.AsSpan(offset + 24, 2048));
            image[offset + 24] = sector == DataSectors - 1 ? (byte)0xD1
                : sector == DataSectors ? (byte)0xA1
                : sector == total - 1 ? (byte)0xA2
                : (byte)0x5A;
        }

        var pvd = 16 * SectorSize + 24;
        image[pvd] = 0x01;
        Encoding.ASCII.GetBytes("CD001").CopyTo(image, pvd + 1);
        image[pvd + 6] = 0x01;
        image[pvd + 80] = (byte)DataSectors;
        image[pvd + 81] = (byte)(DataSectors >> 8);
        image[pvd + 82] = (byte)(DataSectors >> 16);
        image[pvd + 83] = (byte)(DataSectors >> 24);
        return image;
    }

    private static void StampPgd(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var psar = BitConverter.ToInt32(bytes, 0x24);
        bytes[psar + 0x50] = 0x00;
        bytes[psar + 0x51] = 0x50;
        bytes[psar + 0x52] = 0x47;
        bytes[psar + 0x53] = 0x44;
        File.WriteAllBytes(path, bytes);
    }

    private static void CorruptFirstBlock(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = 0x24;
        var psarBuffer = new byte[4];
        stream.ReadExactly(psarBuffer);
        var psar = BitConverter.ToInt32(psarBuffer, 0);
        stream.Position = 0;
        var reader = new PbpReader(stream);
        var block = reader.Discs[0].IsoIndex[0];
        stream.Position = psar + 0x100000 + block.Offset;
        stream.Close();

        using var write = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        write.Position = psar + 0x100000 + block.Offset;
        write.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x11, 0x22, 0x33 });
    }

    private static string Hash(byte[] data, HashAlgorithm algorithm)
    {
        using (algorithm)
        {
            return Convert.ToHexString(algorithm.ComputeHash(data)).ToLowerInvariant();
        }
    }

    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pbpextract-" + Guid.NewGuid().ToString("N"));

        public Workspace()
        {
            Directory.CreateDirectory(Root);
        }

        public string Pack(byte[] image, int compressionLevel)
        {
            var bin = Path.Combine(Root, "disc.bin");
            var cue = Path.Combine(Root, "disc.cue");
            File.WriteAllBytes(bin, image);
            File.WriteAllText(cue, """
                FILE "disc.bin" BINARY
                  TRACK 01 MODE2/2352
                    INDEX 01 00:00:00
                  TRACK 02 AUDIO
                    INDEX 00 00:02:10
                    INDEX 01 00:04:10
                """);

            var resources = FindResources();
            var packed = Path.Combine(Root, "packed");
            Directory.CreateDirectory(packed);
            var options = new ConvertOptions
            {
                DiscInfos = new List<DiscInfo>
                {
                    new()
                    {
                        GameID = "TEST00001",
                        GameTitle = "Synth",
                        SourceIso = bin,
                        SourceToc = cue
                    }
                },
                OutputPath = packed,
                OriginalPath = Root,
                OriginalFilename = "disc",
                MainGameID = "TEST00001",
                MainGameTitle = "Synth",
                MainGameRegion = "NTSC",
                BasePbp = Path.Combine(resources, "BASE.PBP"),
                CompressionLevel = compressionLevel,
                CheckIfFileExists = false,
                SkipIfFileExists = false,
                FileNameFormat = "%FILENAME%",
                Patches = new List<PatchData>(),
                Icon0 = Load(resources, "ICON0.PNG", ResourceType.ICON0),
                Icon1 = Resource.Empty(ResourceType.ICON1),
                Pic0 = Load(resources, "PIC0.PNG", ResourceType.PIC0),
                Pic1 = Load(resources, "PIC1.PNG", ResourceType.PIC1),
                Snd0 = Resource.Empty(ResourceType.SND0),
                Boot = Resource.Empty(ResourceType.BOOT),
                DataPsp = Resource.Empty(ResourceType.PSP)
            };

            var popstation = new Popstation.Popstation
            {
                TempFiles = new List<string>(),
                Notify = (_, _) => { }
            };
            var wrote = popstation.Convert(options, CancellationToken.None);
            Assert.IsTrue(wrote);
            return Path.Combine(packed, "disc.pbp");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }

        private static Resource Load(string resources, string name, ResourceType type)
        {
            var bytes = File.ReadAllBytes(Path.Combine(resources, name));
            return new Resource(type, bytes, (uint)bytes.Length);
        }

        private static string FindResources()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "Popstation", "Resources");
                if (File.Exists(Path.Combine(candidate, "BASE.PBP")))
                {
                    return candidate;
                }

                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException("Popstation resources were not found.");
        }
    }
}
