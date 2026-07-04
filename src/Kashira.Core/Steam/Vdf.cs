using System.Text;

namespace Kashira.Core.Steam;

/// <summary>Valve KeyValues(VDF) 텍스트 노드. libraryfolders.vdf / appmanifest_*.acf 파싱용.</summary>
public sealed class VdfNode
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, VdfNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VdfNode? Child(string key) => Children.TryGetValue(key, out var n) ? n : null;
    public string? Value(string key) => Values.TryGetValue(key, out var v) ? v : null;
}

/// <summary>최소 VDF 파서 (중첩 오브젝트 + 따옴표/역슬래시 이스케이프 + // 주석).</summary>
public static class Vdf
{
    public static VdfNode Parse(string text)
    {
        int i = 0;
        return ParseObject(text, ref i);
    }

    private static VdfNode ParseObject(string s, ref int i)
    {
        var node = new VdfNode();
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] == '}') { if (i < s.Length) i++; break; }

            string key = ReadToken(s, ref i);
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '{')
            {
                i++;
                node.Children[key] = ParseObject(s, ref i);
            }
            else
            {
                node.Values[key] = ReadToken(s, ref i);
            }
        }
        return node;
    }

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) i++;
            else if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '/')
                while (i < s.Length && s[i] != '\n') i++;
            else break;
        }
    }

    private static string ReadToken(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) return string.Empty;

        var sb = new StringBuilder();
        if (s[i] == '"')
        {
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                    sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', var c => c });
                }
                else sb.Append(s[i]);
                i++;
            }
            if (i < s.Length) i++; // 닫는 따옴표
        }
        else
        {
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] != '{' && s[i] != '}')
            { sb.Append(s[i]); i++; }
        }
        return sb.ToString();
    }
}
