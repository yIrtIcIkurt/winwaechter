using System.Text.Json;
using WinWaechter.Core.Changes;
using WinWaechter.Core.Journal;
using WinWaechter.Core.Model;

namespace WinWaechter.Core.Updates;

public enum TelemetryState { On, Off, Unreadable }

/// <summary>
/// winget's user settings file (JSON with comments). Reading skips comments; writing is a targeted text edit
/// that keeps comments, other members and formatting byte-identical, or refuses when the spot is ambiguous.
/// </summary>
public static class WingetSettingsFile
{
    static readonly JsonDocumentOptions Options = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static string PathFor(string localAppData) =>
        Path.Combine(localAppData, "Packages", "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", "LocalState", "settings.json");

    /// <summary>A missing file counts as On (winget's default).</summary>
    public static TelemetryState Read(string path)
    {
        try
        {
            return File.Exists(path) ? ReadText(File.ReadAllText(path)) : TelemetryState.On;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return TelemetryState.Unreadable;
        }
    }

    public static TelemetryState ReadText(string text)
    {
        if (text.Trim().Length == 0) return TelemetryState.On;
        try
        {
            using var doc = JsonDocument.Parse(text, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return TelemetryState.Unreadable;
            return doc.RootElement.TryGetProperty("telemetry", out var t) && t.ValueKind == JsonValueKind.Object
                   && t.TryGetProperty("disable", out var d) && d.ValueKind == JsonValueKind.True
                ? TelemetryState.Off
                : TelemetryState.On;
        }
        catch (JsonException)
        {
            return TelemetryState.Unreadable;
        }
    }

    enum T { Open, Close, OpenArr, CloseArr, Colon, Comma, Str, Lit }

    readonly record struct Token(T Kind, int Start, int End);

    /// <summary>The text with telemetry.disable set; null when the file is broken or the spot is not unique.</summary>
    public static string? SetDisable(string text, bool disable)
    {
        var literal = disable ? "true" : "false";
        if (text.Trim().Length == 0) return "{\n    \"telemetry\": { \"disable\": " + literal + " }\n}";
        try
        {
            using var doc = JsonDocument.Parse(text, Options);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        }
        catch (JsonException)
        {
            return null;
        }

        var tokens = Scan(text);
        if (tokens is null || tokens.Count == 0 || tokens[0].Kind != T.Open) return null;
        var root = tokens[0];

        var telemetry = MembersNamed(tokens, 0, "telemetry", text);
        if (telemetry.Count > 1) return null;
        if (telemetry.Count == 0)
        {
            var hasMembers = tokens.Count > 1 && tokens[1].Kind != T.Close;
            var insert = "\n    \"telemetry\": { \"disable\": " + literal + " }" + (hasMembers ? "," : "\n");
            return text.Insert(root.End, insert);
        }

        var valueIndex = telemetry[0] + 2;   // key, colon, value
        if (valueIndex >= tokens.Count || tokens[valueIndex].Kind != T.Open) return null;
        var disableKeys = MembersNamed(tokens, valueIndex, "disable", text);
        if (disableKeys.Count > 1) return null;
        if (disableKeys.Count == 1)
        {
            var v = disableKeys[0] + 2;
            if (v >= tokens.Count || tokens[v].Kind != T.Lit) return null;
            var current = text[tokens[v].Start..tokens[v].End];
            if (current is not ("true" or "false")) return null;
            return text[..tokens[v].Start] + literal + text[tokens[v].End..];
        }
        var open = tokens[valueIndex];
        var empty = valueIndex + 1 < tokens.Count && tokens[valueIndex + 1].Kind == T.Close;
        return text.Insert(open.End, empty ? " \"disable\": " + literal + " " : " \"disable\": " + literal + ",");
    }

    // Indexes of the key tokens named `name` that are direct members of the object opened at tokens[open].
    static List<int> MembersNamed(List<Token> tokens, int open, string name, string text)
    {
        var found = new List<int>();
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Kind is T.Open or T.OpenArr) { depth++; continue; }
            if (t.Kind is T.Close or T.CloseArr)
            {
                depth--;
                if (depth == 0) break;
                continue;
            }
            if (depth == 1 && t.Kind == T.Str && i + 1 < tokens.Count && tokens[i + 1].Kind == T.Colon
                && text.AsSpan(t.Start + 1, t.End - t.Start - 2).SequenceEqual(name))
                found.Add(i);
        }
        return found;
    }

    // Tokens outside comments; strings keep their quotes. Null on an unterminated string or comment.
    static List<Token>? Scan(string s)
    {
        var list = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == '﻿') { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
            {
                while (i < s.Length && s[i] is not ('\n' or '\r')) i++;
                continue;
            }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
            {
                var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) return null;
                i = end + 2;
                continue;
            }
            if (c == '"')
            {
                var j = i + 1;
                while (j < s.Length && s[j] != '"') j += s[j] == '\\' ? 2 : 1;
                if (j >= s.Length) return null;
                list.Add(new Token(T.Str, i, j + 1));
                i = j + 1;
                continue;
            }
            var kind = c switch
            {
                '{' => T.Open, '}' => T.Close, '[' => T.OpenArr, ']' => T.CloseArr, ':' => T.Colon, ',' => T.Comma, _ => T.Lit,
            };
            if (kind != T.Lit)
            {
                list.Add(new Token(kind, i, i + 1));
                i++;
                continue;
            }
            var k = i;
            while (k < s.Length && !char.IsWhiteSpace(s[k]) && s[k] is not ('{' or '}' or '[' or ']' or ':' or ',' or '"' or '/')) k++;
            list.Add(new Token(T.Lit, i, Math.Max(k, i + 1)));
            i = Math.Max(k, i + 1);
        }
        return list;
    }
}

/// <summary>
/// „winget-Nutzungsdaten abschalten“: telemetry.disable = true in winget's settings file of the signed-in user.
/// Undo writes the exact earlier text back (or removes a file that did not exist before).
/// </summary>
public sealed class WingetTelemetryChange(string path) : IChange
{
    public const string KindName = "wingettelemetry";
    public const string Title = "winget-Nutzungsdaten abschalten";
    public const string AmbiguousText = "Die winget-Einstellungen sind nicht eindeutig lesbar und bleiben unverändert.";

    public string Kind => KindName;
    public string Target => path;
    public string Description => Title;

    public ChangeRecord Apply(SessionContext ctx)
    {
        var existed = File.Exists(path);
        var prior = existed ? File.ReadAllText(path) : null;
        var text = WingetSettingsFile.SetDisable(prior ?? "", true) ?? throw new InvalidOperationException(AmbiguousText);
        Write(text);
        return new ChangeRecord(Guid.NewGuid().ToString("N"), Kind, Target, Description,
            new() { ["existed"] = existed ? "true" : "false", ["text"] = prior }, new() { ["disable"] = "true" },
            ChangeStatus.Applied, DateTimeOffset.Now);
    }

    public void Undo(ChangeRecord record, SessionContext ctx)
    {
        if (record.Before.GetValueOrDefault("existed") == "true") Write(record.Before.GetValueOrDefault("text") ?? "");
        else if (File.Exists(path)) File.Delete(path);
    }

    public DriftState CheckDrift(ChangeRecord record) =>
        WingetSettingsFile.Read(path) == TelemetryState.Off ? DriftState.Unchanged : DriftState.Changed;

    public (Dictionary<string, string?> Before, Dictionary<string, string?> After)? RetryData() => (new(), new());

    void Write(string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".winwaechter.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Only the one settings file of the signed-in user (allowedPath) is accepted.</summary>
    public static WingetTelemetryChange FromRecord(ChangeRecord r, string allowedPath) =>
        string.Equals(r.Target, allowedPath, StringComparison.OrdinalIgnoreCase)
            ? new WingetTelemetryChange(allowedPath)
            : throw new InvalidDataException($"wingettelemetry: unerwartetes Ziel {r.Target}");

    public static void Register(ChangeFactory factory, string allowedPath) =>
        factory.Register(KindName, r => FromRecord(r, allowedPath));
}
