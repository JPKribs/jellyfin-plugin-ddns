using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Utilities;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the save merge: incoming records keep their authored fields, resolve secrets against the stored
/// record by id, get a fresh id when one is missing or duplicated, and a record dropped by the browser is
/// not carried forward.
/// </summary>
public class RecordMergeTests
{
    // Stand-in for the controller's secret resolver: a blank submission keeps the stored value, anything
    // else is "encrypted" by prefixing it so the test can tell kept from re-encrypted.
    private static string Resolve(string? submitted, string? stored)
        => string.IsNullOrEmpty(submitted) ? stored ?? string.Empty : "enc:" + submitted;

    private static Func<string> Ids(params string[] ids)
    {
        var q = new Queue<string>(ids);
        return () => q.Dequeue();
    }

    [Fact]
    public void MatchById_KeepsPriorSecret_EncryptsNew_KeepsAuthoredFields()
    {
        var existing = new List<DNSRecord> { new() { Id = "a", Login = "enc:oldlogin", Password = "enc:oldpass", Hostname = "old" } };
        var incoming = new List<DNSRecord> { new() { Id = "a", Hostname = "new", Login = "", Password = "newpass" } };

        var merged = RecordMerge.Apply(existing, incoming, Resolve, Ids("unused"));

        var r = Assert.Single(merged);
        Assert.Equal("a", r.Id);
        Assert.Equal("new", r.Hostname);          // authored field from incoming
        Assert.Equal("enc:oldlogin", r.Login);    // blank submission kept the stored secret
        Assert.Equal("enc:newpass", r.Password);  // a typed secret was re-encrypted
    }

    [Fact]
    public void MissingId_GetsFreshId()
    {
        var incoming = new List<DNSRecord> { new() { Id = "", Hostname = "x" } };

        var merged = RecordMerge.Apply(new List<DNSRecord>(), incoming, Resolve, Ids("gen1"));

        Assert.Equal("gen1", Assert.Single(merged).Id);
    }

    [Fact]
    public void DuplicateId_SecondGetsFreshUniqueId()
    {
        var incoming = new List<DNSRecord> { new() { Id = "a" }, new() { Id = "a" } };

        var merged = RecordMerge.Apply(new List<DNSRecord>(), incoming, Resolve, Ids("gen1"));

        Assert.Equal("a", merged[0].Id);
        Assert.Equal("gen1", merged[1].Id);
        Assert.NotEqual(merged[0].Id, merged[1].Id);
    }

    [Fact]
    public void RecordDroppedByBrowser_IsNotCarriedForward()
    {
        var existing = new List<DNSRecord> { new() { Id = "a" }, new() { Id = "b" } };
        var incoming = new List<DNSRecord> { new() { Id = "a" } };

        var merged = RecordMerge.Apply(existing, incoming, Resolve, Ids("unused"));

        Assert.Equal(new[] { "a" }, merged.Select(r => r.Id));
    }
}
