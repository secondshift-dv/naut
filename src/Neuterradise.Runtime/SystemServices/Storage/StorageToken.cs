namespace Neuterradise.App.SystemServices.Storage;

public readonly record struct ProfileStorageToken
{
    private const int _minimumHexLength = 6;

    public ProfileStorageToken(string value)
    {
        Validate(value, "P-", _minimumHexLength, nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static void Validate(
        string value,
        string prefix,
        int minimumHexLength,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"A Profile storage token must start with '{prefix}'.", parameterName);
        }

        var hex = value.AsSpan(prefix.Length);
        if (hex.Length < minimumHexLength
            || hex.Length > 64
            || (hex.Length - minimumHexLength) % 2 != 0
            || !IsUpperHex(hex))
        {
            throw new ArgumentException(
                "A Profile storage token must contain 6 to 64 uppercase hexadecimal characters, extended in pairs.",
                parameterName);
        }
    }

    private static bool IsUpperHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}

public readonly record struct MediaStorageToken
{
    private const int _minimumHexLength = 8;

    public MediaStorageToken(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.StartsWith("A-", StringComparison.Ordinal))
        {
            throw new ArgumentException("An Media storage token must start with 'A-'.", nameof(value));
        }

        var hex = value.AsSpan(2);
        if (hex.Length < _minimumHexLength
            || hex.Length > 64
            || (hex.Length - _minimumHexLength) % 2 != 0
            || !IsUpperHex(hex))
        {
            throw new ArgumentException(
                "An Media storage token must contain 8 to 64 uppercase hexadecimal characters, extended in pairs.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;

    private static bool IsUpperHex(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiDigit(character) && character is not (>= 'A' and <= 'F'))
            {
                return false;
            }
        }

        return true;
    }
}
