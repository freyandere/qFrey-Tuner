using System;
using System.Linq;
using QFrey.Core.Contracts;
using QFrey.Core.Workloads;
using Xunit;

namespace QFrey.Tests;

public sealed class OwnedWorkloadTests
{
    private static readonly TargetIdentity Target = new("http://localhost:8080", "v5.2.3", "2.11.3", "2.0.11");
    private static readonly WorkloadCatalogueEntry Ubuntu = WorkloadCatalogue.Get("ubuntu");
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(OwnedWorkloadAction.Stop)]
    [InlineData(OwnedWorkloadAction.Delete)]
    public void CreationJournalAuthorizesOnlyTheExactLiveOwnedItem(OwnedWorkloadAction action)
    {
        var journal = CreateJournal();
        var authorization = journal.Authorize(action, Target, Inventory(journal));

        Assert.True(authorization.Allowed);
        Assert.Null(authorization.ReasonCode);
        Assert.Equal(journal.Id, authorization.Permit!.JournalId);
        Assert.Equal(action, authorization.Permit.Action);
        Assert.Equal(Hash, authorization.Permit.Hash);
        Assert.Equal(Ubuntu.SizeBytes.ToString(), authorization.Permit.TotalBytesDecimal);
    }

    [Fact]
    public void PreflightDoesNotAdoptExistingHashTagOrCategory()
    {
        var intent = Intent();
        var baseItem = Evidence("f" + Hash[1..], Ubuntu.FileName, Ubuntu.SizeBytes.ToString(), intent.ServerSavePath,
            "personal-category", ["personal-tag"]);
        Assert.Equal("WORKLOAD_ALREADY_PRESENT", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [baseItem with { Hash = Hash }])).ReasonCode);
        Assert.Equal("WORKLOAD_ALREADY_PRESENT", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [baseItem with { Tags = [intent.Tag] }])).ReasonCode);
        Assert.Equal("WORKLOAD_ALREADY_PRESENT", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [baseItem with { Category = intent.Category }])).ReasonCode);
    }

    [Fact]
    public void PreflightRejectsMalformedOrAmbiguousIdentityEvidenceAcrossWholeInventory()
    {
        var intent = Intent();
        var unrelated = Evidence("f" + Hash[1..], Ubuntu.FileName, Ubuntu.SizeBytes.ToString(), intent.ServerSavePath,
            "", []);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [unrelated with { Hash = null }])).ReasonCode);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [unrelated with { Hash = new string('g', 40) }])).ReasonCode);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [unrelated with { Category = null }])).ReasonCode);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [unrelated with { Tags = null }])).ReasonCode);
        Assert.Equal("WORKLOAD_AMBIGUOUS", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [unrelated with { Hash = Hash }, unrelated with { Hash = Hash.ToUpperInvariant() }])).ReasonCode);

        var emptyCategory = OwnedWorkloadJournal.CreateNewAddIntent(intent, new(Target, true, [unrelated]));
        Assert.True(emptyCategory.Allowed);
    }

    [Fact]
    public void InventoryAndTagCountsAreBounded()
    {
        var intent = Intent();
        var tooManyRows = new TorrentIdentityEvidence[50_001];
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, tooManyRows)).ReasonCode);

        var tooManyTags = Enumerable.Range(0, 257).Select(i => "tag-" + i).ToArray();
        var item = Evidence("f" + Hash[1..], Ubuntu.FileName, Ubuntu.SizeBytes.ToString(), intent.ServerSavePath,
            "", tooManyTags);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [item])).ReasonCode);
    }

    [Fact]
    public void HashComparisonIsCaseInsensitiveSoUppercaseExistingHashCannotBeAdoptedOrMissed()
    {
        var intent = Intent();
        var existing = Evidence(Hash.ToUpperInvariant(), Ubuntu.FileName, Ubuntu.SizeBytes.ToString(), intent.ServerSavePath,
            "ordinary", []);
        Assert.Equal("WORKLOAD_ALREADY_PRESENT", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [existing])).ReasonCode);

        var journal = CreateJournal();
        var matching = Evidence(journal.Hash.ToUpperInvariant(), journal.Name, journal.TotalBytesDecimal,
            journal.ServerSavePath, journal.Category, [journal.Tag], ServerPathSafety.Unknown);
        Assert.True(journal.Authorize(OwnedWorkloadAction.Stop, Target, new(Target, true, [matching])).Allowed);
    }

    [Fact]
    public void IncompleteOrAmbiguousInventoryNeverCreatesOrAuthorizesJournal()
    {
        var intent = Intent();
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, false, [])).ReasonCode);
        Assert.Equal("WORKLOAD_INVENTORY_INCOMPLETE", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, null)).ReasonCode);

        var journal = CreateJournal();
        var exact = Evidence(journal.Hash, journal.Name, journal.TotalBytesDecimal, journal.ServerSavePath,
            journal.Category, [journal.Tag], ServerPathSafety.VerifiedNoSymlink);
        var duplicateHash = journal.Authorize(OwnedWorkloadAction.Delete, Target,
            new(Target, true, [exact, exact with { Name = "other.iso" }]));
        Assert.Equal("WORKLOAD_AMBIGUOUS", duplicateHash.ReasonCode);

        var duplicateTag = journal.Authorize(OwnedWorkloadAction.Delete, Target,
            new(Target, true, [exact, exact with { Hash = "f" + Hash[1..], Category = "other", Name = "other.iso" }]));
        Assert.Equal("WORKLOAD_AMBIGUOUS", duplicateTag.ReasonCode);
    }

    [Fact]
    public void ReconnectMustMatchFullTargetIdentityAndJournalCannotCrossServers()
    {
        var journal = CreateJournal();
        var wrongEndpoint = Target with { Endpoint = "http://other-host:8080" };
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", journal.Authorize(OwnedWorkloadAction.Stop, wrongEndpoint, Inventory(journal)).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", journal.Authorize(OwnedWorkloadAction.Delete, Target,
            Inventory(journal) with { Target = wrongEndpoint }).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(wrongEndpoint, true, [])).ReasonCode);

        var wrongVersion = Target with { LibtorrentVersion = "other" };
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", journal.Authorize(OwnedWorkloadAction.Stop, wrongVersion, Inventory(journal)).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { Endpoint = "http://user:pass@localhost:8080" }, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { Endpoint = "http://localhost:8080/path?query=1" }, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { Endpoint = "http://localhost:8080/#fragment" }, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { Endpoint = "http://localhost:8080?" }, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { Endpoint = "http://localhost:8080#" }, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_TARGET_MISMATCH", OwnedWorkloadJournal.CreateNewAddIntent(Intent(),
            new(Target with { QbittorrentVersion = "v9.0.0" }, true, [])).ReasonCode);
    }

    [Theory]
    [InlineData("tag", "WORKLOAD_TAG_MISMATCH")]
    [InlineData("category", "WORKLOAD_CATEGORY_MISMATCH")]
    [InlineData("name", "WORKLOAD_NAME_MISMATCH")]
    [InlineData("size", "WORKLOAD_SIZE_MISMATCH")]
    [InlineData("path", "WORKLOAD_PATH_MISMATCH")]
    public void EveryCreationIdentityFieldMustStillMatch(string field, string reason)
    {
        var journal = CreateJournal();
        var evidence = Evidence(journal.Hash, journal.Name, journal.TotalBytesDecimal, journal.ServerSavePath,
            journal.Category, [journal.Tag], ServerPathSafety.VerifiedNoSymlink);
        evidence = field switch
        {
            "tag" => evidence with { Tags = ["qfrey-test-" + new string('f', 32)] },
            "category" => evidence with { Category = "qfrey-test-" + new string('f', 32) },
            "name" => evidence with { Name = "different.iso" },
            "size" => evidence with { TotalBytesDecimal = "1" },
            "path" => evidence with { ServerSavePath = journal.ServerSavePath + "/child" },
            _ => evidence
        };

        Assert.Equal(reason, journal.Authorize(OwnedWorkloadAction.Delete, Target, new(Target, true, [evidence])).ReasonCode);
    }

    [Theory]
    [InlineData(ServerPathSafety.Unknown, "WORKLOAD_PATH_UNVERIFIED")]
    [InlineData(ServerPathSafety.Symlink, "WORKLOAD_PATH_SYMLINK")]
    [InlineData(ServerPathSafety.Mismatch, "WORKLOAD_PATH_UNVERIFIED")]
    public void DeleteRequiresExplicitNonSymlinkServerPathEvidence(ServerPathSafety safety, string reason)
    {
        var journal = CreateJournal();
        var evidence = Evidence(journal.Hash, journal.Name, journal.TotalBytesDecimal, journal.ServerSavePath,
            journal.Category, [journal.Tag], safety);
        Assert.Equal(reason, journal.Authorize(OwnedWorkloadAction.Delete, Target, new(Target, true, [evidence])).ReasonCode);
        Assert.True(journal.Authorize(OwnedWorkloadAction.Stop, Target, new(Target, true, [evidence])).Allowed);
    }

    [Theory]
    [InlineData("relative\\path")]
    [InlineData("C:\\temp\\..\\outside")]
    [InlineData("/var/tmp/../outside")]
    [InlineData("/var/tmp/with\ncontrol")]
    public void NewIntentAndRestoredJournalRejectRelativeTraversalOrControlPaths(string path)
    {
        var intent = Intent() with { ServerSavePath = path };
        Assert.Equal("WORKLOAD_INTENT_INVALID", OwnedWorkloadJournal.CreateNewAddIntent(intent, new(Target, true, [])).ReasonCode);
        Assert.Equal("WORKLOAD_JOURNAL_INVALID", OwnedWorkloadJournal.Restore(CreateJournal().ToData() with { ServerSavePath = path }).ReasonCode);
    }

    [Theory]
    [InlineData("D:\\qfrey\\workload")]
    [InlineData("/srv/qfrey/workload")]
    [InlineData("\\\\server\\share\\qfrey")]
    public void AbsoluteWindowsAndPosixPathsRemainOpaqueAndUnchanged(string path)
    {
        var result = OwnedWorkloadJournal.CreateNewAddIntent(Intent() with { ServerSavePath = path }, new(Target, true, []));
        Assert.True(result.Allowed);
        Assert.Equal(path, result.Journal!.ServerSavePath);
    }

    [Fact]
    public void InvalidJournalMarkersOrMissingLiveItemFailClosed()
    {
        var intent = Intent() with { Tag = "personal-tag" };
        Assert.Equal("WORKLOAD_INTENT_INVALID", OwnedWorkloadJournal.CreateNewAddIntent(intent,
            new(Target, true, [])).ReasonCode);
        var journal = CreateJournal();
        Assert.Equal("WORKLOAD_NOT_FOUND", journal.Authorize(OwnedWorkloadAction.Delete, Target, new(Target, true, [])).ReasonCode);
    }

    [Fact]
    public void PersistedJournalRoundTripsOnlyWithCurrentCatalogueIdentity()
    {
        var journal = CreateJournal();
        var restored = OwnedWorkloadJournal.Restore(journal.ToData());
        Assert.True(restored.Allowed);
        Assert.Equal(journal.Id, restored.Journal!.Id);
        Assert.Equal(journal.Hash, restored.Journal.Hash);
        Assert.Equal("WORKLOAD_JOURNAL_INVALID", OwnedWorkloadJournal.Restore(
            journal.ToData() with { TotalBytesDecimal = "1" }).ReasonCode);
        Assert.Equal("WORKLOAD_JOURNAL_INVALID", OwnedWorkloadJournal.Restore(
            journal.ToData() with { CatalogueConsentIdentity = "changed" }).ReasonCode);
        Assert.Equal("WORKLOAD_JOURNAL_INVALID", OwnedWorkloadJournal.Restore(
            journal.ToData() with { Category = null! }).ReasonCode);
        Assert.Equal("WORKLOAD_JOURNAL_INVALID", OwnedWorkloadJournal.Restore(
            journal.ToData() with { Tag = null! }).ReasonCode);
    }

    private static OwnedWorkloadJournal CreateJournal() => OwnedWorkloadJournal
        .CreateNewAddIntent(Intent(), new(Target, true, []))
        .Journal!;

    private static NewWorkloadIntent Intent()
    {
        var nonce = Guid.NewGuid().ToString("N");
        return new(Target, Ubuntu.Id, new(Ubuntu.FileName, Ubuntu.SizeBytes, Hash),
            "qfrey-test-" + nonce, "qfrey-test-" + nonce, "D:\\qfrey\\workload");
    }

    private static WorkloadInventoryEvidence Inventory(OwnedWorkloadJournal journal) => new(Target, true,
    [Evidence(journal.Hash, journal.Name, journal.TotalBytesDecimal, journal.ServerSavePath,
        journal.Category, [journal.Tag], ServerPathSafety.VerifiedNoSymlink)]);

    private static TorrentIdentityEvidence Evidence(string hash, string name, string size, string path,
        string category, string[]? tags, ServerPathSafety safety = ServerPathSafety.Unknown) =>
        new(hash, name, size, path, category, tags, safety);
}
