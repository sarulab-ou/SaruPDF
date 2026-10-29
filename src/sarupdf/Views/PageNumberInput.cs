using System.Globalization;

namespace SaruPdf.Views;

internal sealed class PageNumberInput
{
    private const int MaximumDigits = 10;
    internal string Text { get; private set; } = "";
    internal bool IsActive => Text.Length != 0;

    internal bool Append(int digit)
    {
        if (digit is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(digit));
        if (Text.Length >= MaximumDigits) return false;
        Text += (char)('0' + digit);
        return true;
    }

    internal void Backspace()
    {
        if (IsActive) Text = Text[..^1];
    }

    internal bool TryGetPage(out int page) =>
        int.TryParse(Text, NumberStyles.None, CultureInfo.InvariantCulture, out page) && page > 0;

    internal void Clear() => Text = "";
}
