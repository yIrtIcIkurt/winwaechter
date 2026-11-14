using System.Text;
using WinWaechter.Core.Programs;
using WinWaechter.Core.Rules;
using WinWaechter.Core.Sys;
using WinWaechter.Core.Updates;
using WinWaechter.Tests.Fakes;

namespace WinWaechter.Tests;

/// <summary>Invented winget output in the real layout (never the owner's program list: the repo is public).</summary>
public static class WingetSamples
{
    // display cells as winget pads them: East-Asian wide characters take two
    static int Cells(string s) => s.Sum(c => c is >= '\u1100' and <= '\u115F' or >= '\u2E80' and <= '\uA4CF'
        or >= '\uAC00' and <= '\uD7A3' or >= '\uF900' and <= '\uFAFF' or >= '\uFF00' and <= '\uFF60' ? 2 : 1);

    static string Pad(string s, int width) => s + new string(' ', Math.Max(1, width - Cells(s)));

    public static string Table(string[] header, params string[][] rows)
    {
        var widths = header.Select((h, i) => Math.Max(Cells(h), rows.Length == 0 ? 0 : rows.Max(r => Cells(r[i]))) + 1).ToArray();
        var sb = new StringBuilder();
        string Line(string[] cols) => string.Concat(cols.Select((c, i) => i == cols.Length - 1 ? c : Pad(c, widths[i]))).TrimEnd();
        sb.Append(Line(header)).Append("\r\n");
        sb.Append(new string('-', widths.Sum())).Append("\r\n");
        foreach (var r in rows) sb.Append(Line(r)).Append("\r\n");
        return sb.ToString();
    }

    public static readonly string[] De = ["Name", "ID", "Version", "Verfügbar", "Quelle"];
    public static readonly string[] DeNoSource = ["Name", "ID", "Version", "Verfügbar"];
    public static readonly string[] En = ["Name", "Id", "Version", "Available", "Source"];

    public static readonly string[][] Basic =
    [
        ["7-Zip 26.00 (x64)", "7zip.7zip", "26.00", "26.06", "winget"],
        ["Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.9.6.4", "8.9.9.1", "winget"],
        ["Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", "Microsoft.VCRedist.2015+.x64", "14.44.35211.0", "14.52.37029.0", "winget"],
    ];

    public static string Spinner => "   - \r   \\ \r   | \r   / \r  ██████████▒▒▒▒  1.00 MB / 2.00 MB\r\n";
}

public class WingetTableTests
{
    static string[] NoSource(string[] r) => r[..4];

    [Fact]
    public void Parses_german_and_english_tables_to_the_same_rows()
    {
        var de = WingetTable.Parse(WingetSamples.Table(WingetSamples.De, WingetSamples.Basic) + "3 Aktualisierungen verfügbar.\r\n");
        var en = WingetTable.Parse(WingetSamples.Table(WingetSamples.En, WingetSamples.Basic) + "3 upgrades available.\r\n");
        Assert.Equal(de, en);
        Assert.Equal(["7zip.7zip", "Notepad++.Notepad++", "Microsoft.VCRedist.2015+.x64"], de.Select(r => r.Id));
        Assert.Equal("14.44.35211.0", de[2].Version);
        Assert.Equal("14.52.37029.0", de[2].Available);
        Assert.Equal("Microsoft Visual C++ 2015-2022 Redistributable (x64) - 14.44.35211", de[2].Name);
    }

    // review: the footer follows the last row directly; in a short table it reaches the last column
    [Fact]
    public void Short_table_footer_is_not_a_package()
    {
        var text = WingetSamples.Table(WingetSamples.DeNoSource, ["Zoom", "Zoom.Zoom", "6.0.2", "6.1.0"]) + "1 Aktualisierungen verfügbar.\r\n";
        var rows = WingetTable.Parse(text);
        Assert.Equal(["Zoom.Zoom"], rows.Select(r => r.Id));
        var en = WingetTable.Parse(WingetSamples.Table(WingetSamples.En, ["Zoom", "Zoom.Zoom", "6.0.2", "6.1.0", "winget"]) + "1 upgrades available.\r\n");
        Assert.Single(en);
    }

    // review: a row whose cells do not start after a space is shifted (e.g. wrongly decoded bytes): never read it
    [Fact]
    public void Shifted_row_is_discarded_not_misread()
    {
        var table = WingetSamples.Table(WingetSamples.DeNoSource,
            ["Ein sehr langer Programmname", "Beispiel.Lang", "2.0", "2.1"],
            ["Groessenpruefer", "Beispiel.PrueferPro", "1.0", "1.1"]);
        // two extra characters in a short name push the later cells right, like UTF-8 bytes read as ANSI („ö“ → „Ã¶“):
        // the ID cell then holds a cut but still valid-looking ID – it must not be taken
        var shifted = table.Replace("Groessenpruefer ", "GrÃ¶ÃŸssenpruefer ");
        var discarded = new List<string>();
        var rows = WingetTable.Parse(shifted, discarded.Add);
        Assert.Equal(["Beispiel.Lang"], rows.Select(r => r.Id));
        Assert.Single(discarded);
    }

    [Fact]
    public void Four_column_table_without_source_is_read()
    {
        var rows = WingetTable.Parse(WingetSamples.Table(WingetSamples.DeNoSource, WingetSamples.Basic.Select(NoSource).ToArray())
                                     + "3 Aktualisierungen verfügbar.\r\n");
        Assert.Equal(3, rows.Count);
        Assert.Equal("26.06", rows[0].Available);
        Assert.Equal("", rows[0].Source);
    }

    [Fact]
    public void Parser_survives_spinner_unicode_names_truncation_and_second_table()
    {
        string[][] rows =
        [
            ["Größenprüfer", "Beispiel.Groessen", "1.0", "1.1", "winget"],
            ["图片工具", "Beispiel.Bilder", "2.0", "2.5", "winget"],
            ["Sehr langes Programm…", "Very.Long.Id…", "3.0", "3.1", "winget"],
            ["Tool", "Beispiel.Tool", "4.0", "4.2", "winget"],
        ];
        var pinned = WingetSamples.Table(WingetSamples.De, ["Gepinnt", "Beispiel.Gepinnt", "1.0", "2.0", "winget"]);
        var text = WingetSamples.Spinner + WingetSamples.Table(WingetSamples.De, rows) + "4 Aktualisierungen verfügbar.\r\n\r\n"
                   + "Die folgenden Pakete haben einen Pin:\r\n" + pinned;
        var discarded = new List<string>();
        var parsed = WingetTable.Parse(text, discarded.Add);
        Assert.Equal(["Beispiel.Groessen", "Beispiel.Bilder", "Beispiel.Tool"], parsed.Select(r => r.Id));
        Assert.Equal("2.5", parsed[1].Available);
        Assert.Equal("图片工具", parsed[1].Name);
        Assert.Single(discarded);
    }

    [Fact]
    public async Task Unknown_version_is_flagged()
    {
        var client = Client(out var runner);
        runner.Handler = (_, args) => Task.FromResult((0, WingetSamples.Table(WingetSamples.DeNoSource,
            ["Ubisoft Connect", "Ubisoft.Connect", "< 175.1.0.13416", "175.1.0.13416"],
            ["Alt", "Beispiel.Alt", "Unknown", "2.0"],
            ["Neu", "Beispiel.Neu", "1.0", "2.0"]), ""));
        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.Ok, list.Status);
        Assert.Equal([true, true, false], list.Packages.Select(p => p.UnknownVersion));
        Assert.Equal("< 175.1.0.13416", list.Packages[0].Installed);
    }

    [Fact]
    public async Task Empty_or_no_table_gives_ok_and_no_packages()
    {
        var client = Client(out var runner);
        runner.Handler = (_, _) => Task.FromResult((unchecked((int)0x8A150014),
            "Es wurde kein installiertes Paket gefunden, das den Eingabekriterien entspricht.\r\n", ""));
        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.Ok, list.Status);
        Assert.Empty(list.Packages);
    }

    [Fact]
    public async Task Source_agreement_prompt_gives_NeedsSourceAgreement()
    {
        var client = Client(out var runner);
        runner.Handler = (_, _) => Task.FromResult((unchecked((int)0x8A150046),
            "Für die Quelle \"winget\" müssen die folgenden Vereinbarungen angezeigt werden …\r\n"
            + "Verwenden Sie --accept-source-agreements, um sie zu akzeptieren.\r\n", ""));
        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.NeedsSourceAgreement, list.Status);
    }

    [Fact]
    public async Task Other_error_gives_Failed_with_text()
    {
        var client = Client(out var runner);
        runner.Handler = (_, _) => Task.FromResult((unchecked((int)0x8A15000B), "Fehler beim Öffnen der Quelle.\r\n", ""));
        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.Failed, list.Status);
        Assert.Contains("0x8A15000B", list.Error);
    }

    [Fact]
    public async Task Missing_winget_path_gives_Missing_without_running_anything()
    {
        var runner = new FakeProcessRunner();
        var client = new WingetClient(runner, new FakeUserProcessRunner(), null, @"C:\Temp");
        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal(WingetListStatus.Missing, list.Status);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task List_marks_user_scope_from_second_call_and_adds_accept_flag_only_when_asked()
    {
        var client = Client(out var runner);
        runner.Handler = (_, args) => Task.FromResult((0, args.Contains("--scope user")
            ? WingetSamples.Table(WingetSamples.DeNoSource, ["Notepad++ (64-bit x64)", "Notepad++.Notepad++", "8.9.6.4", "8.9.9.1"])
            : WingetSamples.Table(WingetSamples.DeNoSource, WingetSamples.Basic.Select(NoSource).ToArray()), ""));

        var list = await client.ListUpgradesAsync(false, CancellationToken.None);
        Assert.Equal([false, true, false], list.Packages.Select(p => p.UserScope));
        Assert.Equal(
        [
            "upgrade --source winget --include-unknown --disable-interactivity",
            "upgrade --source winget --include-unknown --disable-interactivity --scope user",
        ], runner.Calls.Select(c => c.Args));
        Assert.All(runner.Calls, c => Assert.Equal(@"C:\Users\u\AppData\Local\Microsoft\WindowsApps\winget.exe", c.Exe));

        runner.Calls.Clear();
        await client.ListUpgradesAsync(true, CancellationToken.None);
        Assert.All(runner.Calls, c => Assert.EndsWith(" --accept-source-agreements", c.Args));
    }

    [Fact]
    public async Task List_and_upgrade_ask_for_utf8_output()
    {
        var client = Client(out var runner);
        await client.ListUpgradesAsync(false, CancellationToken.None);
        await client.UpgradeAsync(new WingetPackage("7-Zip", "7zip.7zip", "1", "2", false, false), null, CancellationToken.None);
        Assert.Equal(3, runner.Encodings.Count);
        Assert.All(runner.Encodings, e => Assert.Equal(65001, e?.CodePage));
    }

    [Theory]
    [InlineData("a&b")]
    [InlineData("x y")]
    [InlineData(@"..\z")]
    [InlineData("\"q\"")]
    [InlineData("")]
    public async Task Upgrade_rejects_ids_with_metacharacters(string id)
    {
        var client = Client(out var runner);
        var p = new WingetPackage("X", id, "1", "2", false, false);
        await Assert.ThrowsAsync<ArgumentException>(() => client.UpgradeAsync(p, null, CancellationToken.None));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Upgrade_runs_machine_packages_elevated_and_user_packages_in_user_context_with_log()
    {
        var client = Client(out var runner, out var user);
        var lines = new List<string>();
        await client.UpgradeAsync(new WingetPackage("7-Zip", "7zip.7zip", "26.00", "26.06", false, false), lines.Add, CancellationToken.None);
        Assert.Equal("upgrade --id 7zip.7zip --exact --source winget --silent --disable-interactivity --accept-package-agreements --accept-source-agreements",
            runner.Calls.Single().Args);

        user.Handler = (_, args) =>
        {
            var log = args[(args.IndexOf("--log \"", StringComparison.Ordinal) + 7)..].TrimEnd('"');
            File.WriteAllText(log, "Installer gestartet\r\nErfolgreich installiert\r\n");
            return 0;
        };
        var temp = Directory.CreateTempSubdirectory("ww-winget").FullName;
        try
        {
            var userClient = new WingetClient(runner, user, WingetPath, temp);
            var code = await userClient.UpgradeAsync(new WingetPackage("Opera", "Opera.Opera", "1", "2", false, true), lines.Add, CancellationToken.None);
            Assert.Equal(0, code);
            Assert.StartsWith("upgrade --id Opera.Opera --exact --source winget --silent", user.Calls.Single().Args);
            Assert.Contains("Erfolgreich installiert", lines);
            Assert.Empty(Directory.GetFiles(temp));
        }
        finally { Directory.Delete(temp, true); }
    }

    const string WingetPath = @"C:\Users\u\AppData\Local\Microsoft\WindowsApps\winget.exe";

    static WingetClient Client(out FakeProcessRunner runner) => Client(out runner, out _);

    static WingetClient Client(out FakeProcessRunner runner, out FakeUserProcessRunner user)
    {
        runner = new FakeProcessRunner();
        user = new FakeUserProcessRunner();
        return new WingetClient(runner, user, WingetPath, @"C:\Temp");
    }

    static readonly RuleSet Rules = RuleSet.Default;

    static InstalledProgram Installed(string name, string? publisher, string? location = null) =>
        new(Hive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", name, name, publisher, "1", null, null,
            "x", location, null, false, ProgramFlag.None);

    [Fact]
    public void Filter_hides_dell_by_id_and_name_lexware_by_installed_publisher_drivers_and_winwaechter_but_offers_microsoft()
    {
        var all = new List<WingetPackage>
        {
            new("Dell Optimizer", "Dell.Optimizer", "6.3.2.0", "6.3.3.0", false, false),
            new("Display and Peripheral Manager", "Dell.DisplayAndPeripheralManager", "2.1", "2.2", false, false),
            new("Buchhaltung 2026", "Foo.Bar", "1", "2", false, false),
            new("Intel Driver & Support Assistant", "Intel.IntelDriverAndSupportAssistant", "1", "2", false, false),
            new("WinWächter", "TahsinYilmaz.WinWaechter", "0.5.1", "0.6.0", false, false),
            new("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "Microsoft.VCRedist.2015+.x64", "14.44", "14.52", false, false),
            new("PowerToys (Preview) x64", "Microsoft.PowerToys", "0.97.2", "0.102.3101", false, false),
        };
        var filter = new UpdateFilter(Rules, () => [Installed("Buchhaltung 2026", "Haufe-Lexware GmbH & Co. KG")]);
        var (offered, hidden) = filter.Split(all);
        Assert.Equal(["Microsoft.VCRedist.2015+.x64", "Microsoft.PowerToys"], offered.Select(p => p.Id));
        Assert.Equal(5, hidden.Count);
    }

    [Fact]
    public void Filter_hides_by_installed_location_under_lexware()
    {
        var filter = new UpdateFilter(Rules, () => [Installed("Programm X", "Irgendwer", @"C:\Program Files\Lexware\X\")]);
        var (offered, _) = filter.Split([new WingetPackage("Programm X", "Irgend.X", "1", "2", false, false)]);
        Assert.Empty(offered);
    }

    [Fact]
    public void UpdateBlock_from_network_is_ignored_by_UnionWith()
    {
        var bundled = RuleSet.Parse("""{"protected":[{"publisher":"Dell"}],"updateBlock":[{"idPrefix":"Dell."}]}"""u8.ToArray());
        var downloaded = RuleSet.Parse("""{"protected":[{"publisher":"Dell"}],"updateBlock":[{"idPrefix":"Microsoft."}]}"""u8.ToArray());
        var merged = bundled.UnionWith(downloaded);
        Assert.False(merged.IsUpdateBlocked("PowerToys", null, null, "Microsoft.PowerToys"));
        Assert.True(merged.IsUpdateBlocked("Optimizer", null, null, "Dell.Optimizer"));
    }

    // Read-only check on the owner's machine: WW_DEVICE=1 dotnet test --filter Device_. Never runs by default.
    [Fact]
    public async Task Device_real_winget_list_matches_footer_count()
    {
        if (Environment.GetEnvironmentVariable("WW_DEVICE") != "1") return;
        var path = WingetClient.FindWinget(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        Assert.NotNull(path);
        var (_, stdout, _) = await new ProcessRunner().RunAsync(path!, "upgrade --source winget --include-unknown --disable-interactivity", CancellationToken.None);
        var discards = new List<string>();
        var rows = WingetTable.Parse(stdout, discards.Add);
        var footer = System.Text.RegularExpressions.Regex.Match(stdout, @"(\d+) (Aktualisierungen|upgrades) ");
        Assert.True(footer.Success, stdout);
        Assert.Empty(discards);
        Assert.Equal(int.Parse(footer.Groups[1].Value), rows.Count);
    }

    [Theory]
    [InlineData("""{"protected":[{"publisher":"Dell"}],"updateBlock":[{}]}""")]
    [InlineData("""{"protected":[{"publisher":"Dell"}],"updateBlock":[{"idPrefix":"Dell"}]}""")]
    public void Invalid_updateBlock_entries_are_refused(string json)
    {
        Assert.Throws<InvalidDataException>(() => RuleSet.Parse(Encoding.UTF8.GetBytes(json)));
    }
}
