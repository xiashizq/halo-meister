using System.Globalization;

namespace HaloMeister.App.Models;

/// <summary>
/// A lossless view over an INI document. Only the value portion of an edited
/// key/value line is replaced; comments, ordering, whitespace, and unknown lines
/// remain exactly as they were loaded.
/// </summary>
public sealed class IniDocumentModel
{
    private readonly ConfigDocumentAdapter _document;
    private readonly List<string> _lines;
    private readonly string _newLine;

    private IniDocumentModel(ConfigDocumentAdapter document)
    {
        _document = document;
        _newLine = document.Text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = document.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .ToList();

        Settings = ParseSettings();
    }

    public IReadOnlyList<IniSetting> Settings { get; }

    public static IniDocumentModel Parse(Services.ConfigDocument document)
        => new(new ConfigDocumentAdapter(document));

    private IReadOnlyList<IniSetting> ParseSettings()
    {
        var settings = new List<IniSetting>();
        string section = "General";

        for (int lineIndex = 0; lineIndex < _lines.Count; lineIndex++)
        {
            string line = _lines[lineIndex];
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1];
                continue;
            }

            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
                continue;

            int equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
                continue;

            string key = line[..equalsIndex].Trim();
            if (key.Length == 0)
                continue;

            int valueStart = equalsIndex + 1;
            while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart]))
                valueStart++;

            settings.Add(new IniSetting(
                this,
                lineIndex,
                line[..valueStart],
                section,
                key,
                line[valueStart..]));
        }

        return settings;
    }

    internal void SetValue(IniSetting setting, string value)
    {
        if (string.Equals(setting.Value, value, StringComparison.Ordinal))
            return;

        _lines[setting.LineIndex] = setting.LinePrefix + value;
        setting.Value = value;
        _document.Text = string.Join(_newLine, _lines);
    }

    private sealed class ConfigDocumentAdapter
    {
        private readonly Services.ConfigDocument _document;

        public ConfigDocumentAdapter(Services.ConfigDocument document) => _document = document;
        public string Text { get => _document.Text; set => _document.Text = value; }
    }
}

public sealed class IniSetting
{
    private readonly IniDocumentModel _owner;

    internal IniSetting(
        IniDocumentModel owner,
        int lineIndex,
        string linePrefix,
        string section,
        string key,
        string value)
    {
        _owner = owner;
        LineIndex = lineIndex;
        LinePrefix = linePrefix;
        Section = section;
        Key = key;
        Value = value;
    }

    public string Section { get; }
    public string Key { get; }
    public string Value { get; internal set; }
    internal int LineIndex { get; }
    internal string LinePrefix { get; }

    public bool TryGetBoolean(out bool value)
    {
        if (bool.TryParse(Value, out value))
            return true;

        if (IsBooleanLikeKey && Value is "0" or "1")
        {
            value = Value == "1";
            return true;
        }

        return false;
    }

    public bool IsInteger
        => long.TryParse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

    public bool IsFloatingPoint
        => Value.Contains('.') &&
           double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

    public bool IsVector2
    {
        get
        {
            string[] parts = Value.Split(',');
            return parts.Length == 2 &&
                   double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) &&
                   double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        }
    }

    public void SetBoolean(bool value)
    {
        string formatted = Value switch
        {
            "0" or "1" => value ? "1" : "0",
            "true" or "false" => value ? "true" : "false",
            "TRUE" or "FALSE" => value ? "TRUE" : "FALSE",
            _ => value ? "True" : "False",
        };
        _owner.SetValue(this, formatted);
    }

    public void SetValue(string value) => _owner.SetValue(this, value);

    private bool IsBooleanLikeKey
        => Key.Equals("Collapsed", StringComparison.OrdinalIgnoreCase) ||
           Key.Equals("SubtitlesEnabled", StringComparison.OrdinalIgnoreCase) ||
           Key.StartsWith('b') && Key.Length > 1 && char.IsUpper(Key[1]) ||
           Key.EndsWith("Enabled", StringComparison.OrdinalIgnoreCase) ||
           Key.EndsWith("Visible", StringComparison.OrdinalIgnoreCase);
}
