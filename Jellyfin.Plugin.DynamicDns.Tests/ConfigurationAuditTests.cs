using System.Collections.Generic;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.DynamicDns.Models;
using Jellyfin.Plugin.DynamicDns.Services;
using Jellyfin.Plugin.DynamicDns.Utilities;
using MediaBrowser.Model.Activity;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.DynamicDns.Tests;

/// <summary>
/// Covers the configuration save audit: an added or removed record and a changed credential each get one
/// activity log entry, while an unchanged save stays silent. Credentials are compared in stored form, so
/// an echoed back stored value is not a change.
/// </summary>
public class ConfigurationAuditTests
{
    private readonly List<ActivityLog> _entries = new();
    private readonly ActivityLogger _activity;

    public ConfigurationAuditTests()
    {
        var manager = Substitute.For<IActivityManager>();
        manager.CreateAsync(Arg.Do<ActivityLog>(_entries.Add));
        _activity = new ActivityLogger(manager, Substitute.For<ILogger<ActivityLogger>>());
    }

    private static DNSRecord Record(string id, string host = "host.example.com", string login = "enc:l", string password = "enc:p")
        => new() { Id = id, Hostname = host, Login = login, Password = password };

    [Fact]
    public void UnchangedSave_LogsNothing()
    {
        var records = new List<DNSRecord> { Record("a") };

        ConfigurationAudit.LogRecordChanges(_activity, records, new List<DNSRecord> { Record("a") });

        Assert.Empty(_entries);
    }

    [Fact]
    public void NewRecord_LogsAdded()
    {
        ConfigurationAudit.LogRecordChanges(_activity, new List<DNSRecord>(), new List<DNSRecord> { Record("a") });

        var entry = Assert.Single(_entries);
        Assert.Equal("DynamicDNS.RecordAdded", entry.Type);
        Assert.Contains("host.example.com", entry.Name, System.StringComparison.Ordinal);
    }

    [Fact]
    public void DroppedRecord_LogsRemoved()
    {
        ConfigurationAudit.LogRecordChanges(_activity, new List<DNSRecord> { Record("a") }, new List<DNSRecord>());

        var entry = Assert.Single(_entries);
        Assert.Equal("DynamicDNS.RecordRemoved", entry.Type);
    }

    [Fact]
    public void ChangedPassword_LogsCredentialsChanged_Once()
    {
        var before = new List<DNSRecord> { Record("a") };
        var after = new List<DNSRecord> { Record("a", password: "enc:replaced") };

        ConfigurationAudit.LogRecordChanges(_activity, before, after);

        var entry = Assert.Single(_entries);
        Assert.Equal("DynamicDNS.RecordCredentialsChanged", entry.Type);
    }

    [Fact]
    public void ChangedLogin_LogsCredentialsChanged()
    {
        ConfigurationAudit.LogRecordChanges(
            _activity,
            new List<DNSRecord> { Record("a") },
            new List<DNSRecord> { Record("a", login: "enc:replaced") });

        Assert.Equal("DynamicDNS.RecordCredentialsChanged", Assert.Single(_entries).Type);
    }

    [Fact]
    public void FirstSave_NullBefore_LogsEveryRecordAsAdded()
    {
        ConfigurationAudit.LogRecordChanges(_activity, null, new List<DNSRecord> { Record("a"), Record("b") });

        Assert.Equal(2, _entries.Count);
        Assert.All(_entries, e => Assert.Equal("DynamicDNS.RecordAdded", e.Type));
    }

    [Fact]
    public void MixedSave_LogsEachChangeSeparately()
    {
        var before = new List<DNSRecord> { Record("keep"), Record("drop"), Record("cred") };
        var after = new List<DNSRecord> { Record("keep"), Record("cred", password: "enc:new"), Record("fresh") };

        ConfigurationAudit.LogRecordChanges(_activity, before, after);

        Assert.Equal(3, _entries.Count);
        Assert.Single(_entries, e => e.Type == "DynamicDNS.RecordAdded");
        Assert.Single(_entries, e => e.Type == "DynamicDNS.RecordRemoved");
        Assert.Single(_entries, e => e.Type == "DynamicDNS.RecordCredentialsChanged");
    }

    [Fact]
    public void NullActivityLogger_DoesNotThrow()
    {
        ConfigurationAudit.LogRecordChanges(null, new List<DNSRecord> { Record("a") }, new List<DNSRecord>());
    }
}
