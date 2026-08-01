using System.Text;

namespace HaloMeister.App.Models;

/// <summary>
/// Parser for Unreal's compact config-value notation, for example
/// (VitalityTraits=(DamageResistance=Invulnerable),WeaponTraits=()).
/// </summary>
public abstract class UnrealConfigValue
{
    public abstract string Serialize();

    public static bool TryParse(string text, out UnrealConfigValue? value)
    {
        try
        {
            var parser = new Parser(text);
            value = parser.ParseValue();
            parser.SkipWhiteSpace();
            return parser.IsAtEnd;
        }
        catch (FormatException)
        {
            value = null;
            return false;
        }
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _position;

        public Parser(string text) => _text = text;
        public bool IsAtEnd => _position == _text.Length;

        public UnrealConfigValue ParseValue()
        {
            SkipWhiteSpace();
            if (IsAtEnd)
                return new UnrealScalarValue(string.Empty, isQuoted: false);
            if (_text[_position] == '(')
                return ParseContainer();
            if (_text[_position] == '"')
                return ParseQuoted();
            return ParseBare();
        }

        public void SkipWhiteSpace()
        {
            while (!IsAtEnd && char.IsWhiteSpace(_text[_position]))
                _position++;
        }

        private UnrealContainerValue ParseContainer()
        {
            Expect('(');
            var entries = new List<UnrealConfigEntry>();
            SkipWhiteSpace();
            if (TryConsume(')'))
                return new UnrealContainerValue(entries);

            while (true)
            {
                SkipWhiteSpace();
                int start = _position;
                string? name = TryParseName();
                if (name is null)
                    _position = start;

                UnrealConfigValue child = ParseValue();
                entries.Add(new UnrealConfigEntry(name, child));

                SkipWhiteSpace();
                if (TryConsume(')'))
                    break;
                Expect(',');
            }

            return new UnrealContainerValue(entries);
        }

        private string? TryParseName()
        {
            int start = _position;
            while (!IsAtEnd && _text[_position] is not '=' and not ',' and not '(' and not ')')
                _position++;

            if (IsAtEnd || _text[_position] != '=')
            {
                _position = start;
                return null;
            }

            string name = _text[start.._position].Trim();
            if (name.Length == 0)
            {
                _position = start;
                return null;
            }

            _position++;
            return name;
        }

        private UnrealScalarValue ParseQuoted()
        {
            Expect('"');
            var value = new StringBuilder();
            while (!IsAtEnd)
            {
                char character = _text[_position++];
                if (character == '"')
                    return new UnrealScalarValue(value.ToString(), isQuoted: true);

                if (character == '\\' && !IsAtEnd)
                {
                    char escaped = _text[_position++];
                    value.Append(escaped);
                }
                else
                {
                    value.Append(character);
                }
            }

            throw new FormatException("Unterminated quoted Unreal config value.");
        }

        private UnrealScalarValue ParseBare()
        {
            int start = _position;
            while (!IsAtEnd && _text[_position] is not ',' and not ')')
                _position++;
            return new UnrealScalarValue(_text[start.._position].Trim(), isQuoted: false);
        }

        private bool TryConsume(char expected)
        {
            if (!IsAtEnd && _text[_position] == expected)
            {
                _position++;
                return true;
            }
            return false;
        }

        private void Expect(char expected)
        {
            if (!TryConsume(expected))
                throw new FormatException($"Expected '{expected}' at position {_position}.");
        }
    }
}

public sealed class UnrealContainerValue(List<UnrealConfigEntry> entries) : UnrealConfigValue
{
    public List<UnrealConfigEntry> Entries { get; } = entries;
    public bool IsNamedObject => Entries.Count > 0 && Entries.All(entry => entry.Name is not null);
    public bool IsList => Entries.Count > 0 && Entries.All(entry => entry.Name is null);

    public override string Serialize()
        => $"({string.Join(",", Entries.Select(entry =>
            entry.Name is null ? entry.Value.Serialize() : $"{entry.Name}={entry.Value.Serialize()}"))})";
}

public sealed class UnrealScalarValue(string value, bool isQuoted) : UnrealConfigValue
{
    public string Value { get; set; } = value;
    public bool IsQuoted { get; } = isQuoted;

    public override string Serialize()
        => IsQuoted
            ? $"\"{Value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : Value;
}

public sealed record UnrealConfigEntry(string? Name, UnrealConfigValue Value);
