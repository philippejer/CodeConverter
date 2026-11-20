using System;

/// <summary>VB keywords are case-insensitive, and so are VB Razor's: @Code, @code and @CODE are the same directive.</summary>
internal static class VbKeyword
{
    public static bool Is(string text, string keyword) => string.Equals(text, keyword, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the text starts with the keyword, followed by whitespace when <paramref name="keyword"/> ends with a space.</summary>
    public static bool StartsWith(string text, string keyword) => text.StartsWith(keyword, StringComparison.OrdinalIgnoreCase);
}
