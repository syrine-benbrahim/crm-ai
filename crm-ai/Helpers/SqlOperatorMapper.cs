namespace crm_ai.Helpers
{
    public static class SqlOperatorMapper
    {
        public static string MapString(string op, string field, string value)
        {
            return op switch
            {
                "IS" or "=" => $"{field} = '{value}'",
                "IS NOT" or "!=" => $"{field} != '{value}'",
                "CONTAINS" => $"{field} LIKE '%{value}%'",
                "STARTS WITH" => $"{field} LIKE '{value}%'",
                "ENDS WITH" => $"{field} LIKE '%{value}'",
                _ => $"{field} = '{value}'"
            };
        }

        public static string MapNumber(string op, string field, string value)
        {
            return op switch
            {
                "IS" or "=" => $"{field} = {value}",
                "IS NOT" or "!=" => $"{field} != {value}",
                ">" => $"{field} > {value}",
                "<" => $"{field} < {value}",
                ">=" => $"{field} >= {value}",
                "<=" => $"{field} <= {value}",
                _ => $"{field} = {value}"
            };
        }
    }
}