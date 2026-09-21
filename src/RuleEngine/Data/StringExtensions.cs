namespace Templates
{
    public static class StringExtensions
    {
        public static string? GetNestedString(this string str, char start = '{', char end = '}')
        {
            int s = -1;
            int i = -1;
            while (++i < str.Length)
                if (str[i] == start)
                {
                    s = i;
                    break;
                }
            int e = -1;
            int depth = 0;
            while (++i < str.Length)
                if (str[i] == end)
                {
                    e = i;
                    if (depth == 0)
                        break;
                    else
                        --depth;
                }
                else if (str[i] == start)
                    ++depth;
            if (e > s)
                return str.Substring(s + 1, e - s - 1);
            return null;
        }



        // The ago() offset in seconds, parsed once per template version so the compiled engine can read
        // the clock per evaluation. The sign is applied by the caller (CompiledStringMatcher, D-6/P-14).
        public static long GetAgoOffsetSeconds(this string val)
        {
            var substring = val.GetNestedString() ?? throw new InvalidOperationException("Invalid ago must be dhm eg ago(4d)");
            substring = substring.Replace("ago(", string.Empty).Replace(")", string.Empty);

            var mult = substring[^1] switch
            {
                'd' => 24 * 3600,
                'h' => 3600,
                'm' => 60,
                _ => throw new InvalidOperationException("Invalid ago must be dhm eg ago(4d)")
            };
            var agoValue = long.Parse(val[5..^3].Trim());
            return mult * agoValue;
        }
    }
}
