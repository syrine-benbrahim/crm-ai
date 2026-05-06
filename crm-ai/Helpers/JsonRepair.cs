using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace crm_ai.Helpers
{
    public static class JsonRepair
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
        };

        public static string Clean(string raw, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(raw)) return raw;

            var s = raw.Trim();
            if (s.StartsWith("```json")) s = s[7..];
            else if (s.StartsWith("```")) s = s[3..];
            if (s.EndsWith("```")) s = s[..^3];
            s = s.Trim().TrimEnd(',');

            if (s.TrimStart().StartsWith("[")) return s;

            if (TryParseObject(s, out _)) return s;

            int start = s.IndexOf('{');
            int end = s.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var extracted = s[start..(end + 1)];
                if (TryParseObject(extracted, out _)) return extracted;

                var repaired = Repair(extracted);
                if (TryParseObject(repaired, out _))
                {
                    logger.LogInformation("JsonRepair strategy 3 (Repair) succeeded");
                    return repaired;
                }

                var longest = LongestBalanced(s);
                if (longest != null && TryParseObject(longest, out _))
                {
                    logger.LogInformation("JsonRepair strategy 4 (LongestBalanced) succeeded");
                    return longest;
                }

                logger.LogError(
                    "JsonRepair: all strategies failed — returning best-effort");
                return repaired;
            }

            logger.LogError(
                "JsonRepair: no {{}} block in: {Raw}",
                raw[..Math.Min(200, raw.Length)]);
            return s;
        }

        public static bool TryParseObject(string s, out JsonObject? result)
        {
            try
            {
                result = JsonSerializer.Deserialize<JsonObject>(s, JsonOptions);
                return result != null;
            }
            catch { result = null; return false; }
        }

        private static string Repair(string s)
        {
            var stack = new Stack<char>();
            var output = new StringBuilder();
            bool inString = false, escape = false;

            foreach (char c in s)
            {
                if (escape) { escape = false; output.Append(c); continue; }
                if (c == '\\' && inString) { escape = true; output.Append(c); continue; }
                if (c == '"') { inString = !inString; output.Append(c); continue; }
                if (inString) { output.Append(c); continue; }

                switch (c)
                {
                    case '{': stack.Push('}'); output.Append(c); break;
                    case '[': stack.Push(']'); output.Append(c); break;
                    case '}':
                    case ']':
                        if (stack.Count > 0 && stack.Peek() == c)
                        { stack.Pop(); output.Append(c); }
                        else if (stack.Count > 0)
                        { output.Append(stack.Pop()); }
                        break;
                    default: output.Append(c); break;
                }
            }

            while (stack.Count > 0) output.Append(stack.Pop());
            return output.ToString();
        }

        private static string? LongestBalanced(string s)
        {
            string? best = null;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '{') continue;
                int depth = 0; bool inStr = false, esc = false;
                for (int j = i; j < s.Length; j++)
                {
                    char c = s[j];
                    if (esc) { esc = false; continue; }
                    if (c == '\\' && inStr) { esc = true; continue; }
                    if (c == '"') { inStr = !inStr; continue; }
                    if (inStr) continue;
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    if (depth == 0)
                    {
                        var candidate = s[i..(j + 1)];
                        if (best == null || candidate.Length > best.Length)
                            best = candidate;
                        break;
                    }
                }
            }
            return best;
        }
    }
}