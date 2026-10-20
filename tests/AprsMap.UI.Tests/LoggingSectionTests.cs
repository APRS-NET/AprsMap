// SPDX-License-Identifier: AGPL-3.0-only
using System.Globalization;
using AprsMap.Contracts;
using AprsMap.UI.Localization;
using AprsMap.UI.Settings;

namespace AprsMap.UI.Tests;

/// <summary>Settings › Logging (docs/features/F1.32-logging.md).</summary>
public sealed class LoggingSectionTests
{
    private static (LoggingSectionViewModel Section, FakeEngine Engine) Create(LoggingSettings? logging = null)
    {
        var engine = new FakeEngine();
        engine.Settings = engine.Settings with { Logging = logging };
        return (
            new LoggingSectionViewModel(engine, new Localizer(CultureInfo.GetCultureInfo("en-US")), _ => { }),
            engine
        );
    }

    [Fact]
    public async Task NewSettingsShowInformationMapTilesAtWarningsAndTheDefaultFolder()
    {
        var (section, engine) = Create();

        await section.ShowAsync(engine.Settings);

        Assert.Equal("Information", section.SelectedLevel!.Name);
        Assert.Equal("Warnings", section.Areas.Single(a => a.Area == LogArea.Tiles).Selected!.Name);
        Assert.Equal("Same as overall", section.Areas.Single(a => a.Area == LogArea.Connections).Selected!.Name);
        Assert.False(section.Packets || section.MessageTexts);
        Assert.True(section.FileOn); // the desktop's default
        Assert.Equal(string.Empty, section.Folder);
        Assert.Equal(engine.LoggingStatus.DefaultFolder, section.DefaultFolder);
        Assert.Equal(("10", "14", "100"), (section.MaxFileMegabytes, section.KeepDays, section.TotalMegabytes));
    }

    [Fact]
    public async Task WhatTheEnvironmentSetsCantBeChangedHere()
    {
        var (section, engine) = Create();
        engine.LoggingStatus = engine.LoggingStatus with
        {
            EnvironmentLevel = true,
            EnvironmentAreas = [LogArea.Web],
            EnvironmentEndpoint = "http://collector:4317",
        };

        await section.ShowAsync(engine.Settings);

        Assert.True(section.LevelFromEnvironment);
        Assert.False(section.Areas.Single(a => a.Area == LogArea.Web).CanChange);
        Assert.Equal("Set by the environment: http://collector:4317", section.EnvironmentTarget);
    }

    [Fact]
    public async Task SaveSendsTheLevelsTheFileAndATargetKeepingAStoredHeader()
    {
        var stored = new OtlpTarget(
            "t",
            "Seq",
            true,
            "http://seq.local:5341",
            Headers: [new OtlpHeader("X-Key", null, true)]
        );
        var (section, engine) = Create(new LoggingSettings(Targets: [stored]));
        await section.ShowAsync(engine.Settings);

        section.SelectedLevel = section.Levels.Single(l => l.Level == LogLevelSetting.Debug);
        var connections = section.Areas.Single(a => a.Area == LogArea.Connections);
        connections.Selected = connections.Choices.Single(c => c.Level == LogLevelSetting.Trace);
        section.MessageTexts = true;
        section.KeepDays = "30";
        section.Targets[0].EditCommand.Execute(null);
        Assert.True(Assert.Single(section.EditHeaders).HasStoredValue); // shown as saved, never its value
        section.EditGrpc = false;
        section.KeepTargetCommand.Execute(null);
        await section.SaveCommand.ExecuteAsync(null);

        var logging = Assert.Single(engine.Saved).EffectiveLogging;
        Assert.Equal(LogLevelSetting.Debug, logging.Level);
        Assert.Equal(LogLevelSetting.Trace, logging.LevelOf(LogArea.Connections));
        Assert.Equal(LogLevelSetting.Warnings, logging.LevelOf(LogArea.Tiles));
        Assert.True(logging.MessageTexts);
        Assert.Equal(30, logging.EffectiveFile.KeepDays);
        Assert.Null(logging.EffectiveFile.On); // still the default
        var target = Assert.Single(logging.TargetList);
        Assert.Equal(OtlpProtocol.HttpProtobuf, target.Protocol);
        Assert.Equal(
            ("X-Key", null, true),
            (target.HeaderList[0].Name, target.HeaderList[0].Value, target.HeaderList[0].HasValue)
        );
        Assert.Equal("Saved", section.SavedText);
    }

    [Fact]
    public async Task ALimitThatIsntANumberIsRefusedHere()
    {
        var (section, engine) = Create();
        await section.ShowAsync(engine.Settings);
        section.MaxFileMegabytes = "ten";

        await section.SaveCommand.ExecuteAsync(null);

        Assert.Empty(engine.Saved);
        Assert.Equal("Type whole numbers for the log file's limits.", section.Error);
    }
}
