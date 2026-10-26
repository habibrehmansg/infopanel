namespace InfoPanel.Spec;

/// <summary>
/// The time patterns of <c>formatTime</c> (spec/docs/expressions.md, "Time patterns"): runs of the pattern letters
/// y (1, 2 or 4), M (1–4), d (1–2), E (1, 3 or 4), H, h, m, s (1–2), S (1–3) and a (1); text in single quotes ('' is a
/// quote); every other ASCII letter is reserved; any other character stands for itself.
/// </summary>
public static class TimePattern
{
    public static bool IsValid(string pattern)
    {
        var i = 0;
        while (i < pattern.Length)
        {
            var c = pattern[i];
            if (c == '\'')
            {
                var end = pattern.IndexOf('\'', i + 1);
                if (end < 0) return false;
                i = end + 1;
                continue;
            }
            if (!char.IsAsciiLetter(c))
            {
                i++;
                continue;
            }
            var run = 1;
            while (i + run < pattern.Length && pattern[i + run] == c) run++;
            var ok = c switch
            {
                'y' => run is 1 or 2 or 4,
                'M' => run <= 4,
                'E' => run is 1 or 3 or 4,
                'd' or 'H' or 'h' or 'm' or 's' => run <= 2,
                'S' => run <= 3,
                'a' => run == 1,
                _ => false,
            };
            if (!ok) return false;
            i += run;
        }
        return true;
    }
}
