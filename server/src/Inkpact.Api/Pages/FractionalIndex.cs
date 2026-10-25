using System.Text;

namespace Inkpact.Api.Pages;

/// <summary>
/// Order keys for siblings: strings that sort in the order the pages are shown, and that always leave room
/// between two neighbours, so dragging a page writes one row and never renumbers the others.
///
/// A port of <c>generateKeyBetween</c> and <c>generateNKeysBetween</c> from the "fractional-indexing" package
/// (https://github.com/rocicorp/fractional-indexing, version 4.0.0, CC0, public domain) with its default alphabets.
/// No maintained .NET library exists, so the architecture asks for a port tested against the original: the
/// vectors in <c>Fixtures/fractional-index-vectors.json</c> were made by the original, and every key here must be
/// byte for byte the one it gives. Keys compare as plain strings, which is what the "C" collation of the database
/// column does.
///
/// A key is an integer part, whose first character says how long it is (A-Z for the long negative ones, a-z for the
/// positive ones), then a fraction of base-62 digits that never ends in a zero.
/// </summary>
public static class FractionalIndex
{
    /// <summary>The digits of the fraction and of the rest of the integer part, in sort order.</summary>
    private const string Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>The first characters of the integer part: A-Z mark negative lengths, a-z positive ones.</summary>
    private const string Heads = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    private const char Zero = '0';

    /// <summary>
    /// A key that sorts between <paramref name="a"/> (the one before, or null at the start) and <paramref name="b"/>
    /// (the one after, or null at the end). Throws <see cref="FormatException"/> for a key that is not valid and
    /// <see cref="ArgumentException"/> for bounds that are equal.
    /// </summary>
    public static string KeyBetween(string? a, string? b)
    {
        if (a is not null)
        {
            ValidateOrderKey(a);
        }

        if (b is not null)
        {
            ValidateOrderKey(b);
        }

        if (a is not null && b is not null && string.CompareOrdinal(a, b) > 0)
        {
            // The original swaps bounds that are the wrong way round; so does this.
            (a, b) = (b, a);
        }

        if (a is null)
        {
            if (b is null)
            {
                return Heads[Heads.Length / 2].ToString() + Zero;
            }

            var integerB = GetIntegerPart(b);
            var fractionB = b[integerB.Length..];
            if (IsSmallestInteger(integerB))
            {
                return integerB + Midpoint("", fractionB);
            }

            if (string.CompareOrdinal(integerB, b) < 0)
            {
                return integerB;
            }

            return DecrementInteger(integerB) ?? throw new InvalidOperationException("cannot decrement any more");
        }

        if (b is null)
        {
            var integerA = GetIntegerPart(a);
            var fractionA = a[integerA.Length..];
            return IncrementInteger(integerA) ?? integerA + Midpoint(fractionA, null);
        }

        var ia = GetIntegerPart(a);
        var fa = a[ia.Length..];
        var ib = GetIntegerPart(b);
        var fb = b[ib.Length..];
        if (ia == ib)
        {
            return ia + Midpoint(fa, fb);
        }

        var next = IncrementInteger(ia) ?? throw new InvalidOperationException("cannot increment any more");
        return string.CompareOrdinal(next, b) < 0 ? next : ia + Midpoint(fa, null);
    }

    /// <summary>
    /// <paramref name="count"/> distinct keys in order between <paramref name="a"/> and <paramref name="b"/>. With one
    /// bound open they are consecutive short keys; with both given they are as short as the room allows.
    /// </summary>
    public static IReadOnlyList<string> KeysBetween(string? a, string? b, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count == 0)
        {
            return [];
        }

        if (count == 1)
        {
            return [KeyBetween(a, b)];
        }

        if (b is null)
        {
            var key = KeyBetween(a, b);
            var after = new List<string>(count) { key };
            for (var i = 0; i < count - 1; i++)
            {
                key = KeyBetween(key, b);
                after.Add(key);
            }

            return after;
        }

        if (a is null)
        {
            var key = KeyBetween(a, b);
            var before = new List<string>(count) { key };
            for (var i = 0; i < count - 1; i++)
            {
                key = KeyBetween(a, key);
                before.Add(key);
            }

            before.Reverse();
            return before;
        }

        var middle = count / 2;
        var c = KeyBetween(a, b);
        return [.. KeysBetween(a, c, middle), c, .. KeysBetween(c, b, count - middle - 1)];
    }

    /// <summary>True when the text is a key this class could have made.</summary>
    public static bool IsValid(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        try
        {
            ValidateOrderKey(key);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    // `a` may be empty, `b` is null or not empty, and a < b when b is given. Neither ends in a zero.
    // Returns a fraction that sorts between them.
    private static string Midpoint(string a, string? b)
    {
        if (b is not null && string.CompareOrdinal(a, b) >= 0)
        {
            throw new ArgumentException($"{a} >= {b}");
        }

        if (a.Length > 0 && a[^1] == Zero || b is { Length: > 0 } && b[^1] == Zero)
        {
            throw new FormatException("trailing zero");
        }

        if (b is not null)
        {
            // Take the longest common prefix off both, padding `a` with zeros as we go.
            // `b` needs no padding: it cannot end before `a` while they are the same.
            var n = 0;
            while (n < b.Length && CharAt(a, n) == b[n])
            {
                n++;
            }

            if (n > 0)
            {
                return b[..n] + Midpoint(a[Math.Min(n, a.Length)..], b[n..]);
            }
        }

        // The first digits (or the lack of one) differ.
        var digitA = a.Length > 0 ? DigitValue(a[0]) : 0;
        var digitB = b is not null ? DigitValue(b[0]) : Digits.Length;
        if (digitB - digitA > 1)
        {
            // Math.round(0.5 * (digitA + digitB)) in the original, which rounds a half up.
            return Digits[(digitA + digitB + 1) / 2].ToString();
        }

        // The first digits are consecutive.
        if (b is { Length: > 1 })
        {
            return b[..1];
        }

        // `b` is null or a single digit. The first digit of `a` is the one before it, or the last digit if `b`
        // is null. For midpoint("49", "5") that gives "4" + midpoint("9", null), which becomes "495".
        return Digits[digitA] + Midpoint(a.Length > 0 ? a[1..] : "", null);
    }

    private static char CharAt(string s, int index) => index < s.Length ? s[index] : Zero;

    private static int DigitValue(char c)
    {
        var value = Digits.IndexOf(c, StringComparison.Ordinal);
        return value >= 0 ? value : throw new FormatException($"invalid digit: {c}");
    }

    private static void ValidateInteger(string integer)
    {
        if (integer.Length != IntegerLength(integer[0]))
        {
            throw new FormatException($"invalid integer part of order key: {integer}");
        }
    }

    // How long the integer part is, from its first character: 2 for the heads next to the middle, up to 27.
    private static int IntegerLength(char head)
    {
        if (head is >= 'a' and <= 'z')
        {
            return head - 'a' + 2;
        }

        if (head is >= 'A' and <= 'Z')
        {
            return 'Z' - head + 2;
        }

        throw new FormatException($"invalid order key head: {head}");
    }

    private static string GetIntegerPart(string key)
    {
        var length = IntegerLength(key[0]);
        return length > key.Length ? throw new FormatException($"invalid order key: {key}") : key[..length];
    }

    private static void ValidateOrderKey(string key)
    {
        if (key.Length == 0)
        {
            throw new FormatException("an order key cannot be empty");
        }

        foreach (var c in key)
        {
            if (!char.IsAsciiLetterOrDigit(c))
            {
                throw new FormatException($"invalid character in order key: {key}");
            }
        }

        if (IsSmallestInteger(key))
        {
            throw new FormatException($"invalid order key: {key}");
        }

        var integer = GetIntegerPart(key);
        var fraction = key[integer.Length..];
        if (fraction.Length > 0 && fraction[^1] == Zero)
        {
            throw new FormatException($"invalid order key: {key}");
        }
    }

    // The integer after this one, or null for the very last one.
    private static string? IncrementInteger(string x)
    {
        ValidateInteger(x);
        var head = x[0];
        // Walk the digits from the right, turning the ones at their maximum into zeros until one can be raised.
        var trailing = "";
        for (var i = x.Length - 1; i >= 1; i--)
        {
            var d = DigitValue(x[i]) + 1;
            if (d == Digits.Length)
            {
                trailing = Zero + trailing;
            }
            else
            {
                return head + x[1..i] + Digits[d] + trailing;
            }
        }

        // The carry went out of every digit; `trailing` is all zeros now.
        var headIndex = Heads.IndexOf(head, StringComparison.Ordinal);
        if (headIndex == Heads.Length - 1)
        {
            return null;
        }

        var h = Heads[headIndex + 1];
        var lengthDelta = IntegerLength(h) - IntegerLength(head);
        return h + (lengthDelta > 0 ? trailing + Zero : lengthDelta < 0 ? trailing[1..] : trailing);
    }

    // The integer before this one, or null for the very first one.
    private static string? DecrementInteger(string x)
    {
        ValidateInteger(x);
        var head = x[0];
        var last = Digits[^1];
        // Walk the digits from the right, turning the ones at zero into the maximum until one can be lowered.
        var trailing = "";
        for (var i = x.Length - 1; i >= 1; i--)
        {
            var d = DigitValue(x[i]) - 1;
            if (d == -1)
            {
                trailing = last + trailing;
            }
            else
            {
                return head + x[1..i] + Digits[d] + trailing;
            }
        }

        var headIndex = Heads.IndexOf(head, StringComparison.Ordinal);
        if (headIndex == 0)
        {
            return null;
        }

        var h = Heads[headIndex - 1];
        var lengthDelta = IntegerLength(h) - IntegerLength(head);
        return h + (lengthDelta > 0 ? trailing + last : lengthDelta < 0 ? trailing[1..] : trailing);
    }

    // The smallest integer is the most negative head with all zeros after it. It cannot be a key, because
    // nothing could be put before it.
    private static bool IsSmallestInteger(string key) =>
        key == Heads[0] + new string(Zero, Heads.Length / 2);
}
