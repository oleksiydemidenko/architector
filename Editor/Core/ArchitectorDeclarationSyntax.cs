using System;
using System.Collections.Generic;

namespace Architector.Editor
{
    public enum ArchitectorCodeRole
    {
        Plain,
        Keyword,
        Type,
        Member,
        Parameter,
        Literal
    }

    // Splits a member declaration as Architector writes it ("protected override void OnAdded()", "public Boolean
    // CanUse(IUnit player, UInt64 clientId)", "public IUnit Holder { get; private set; }") into colored parts: keywords
    // and modifiers, type names, the member name, parameter names and literals. It reads any text without failing, so
    // manual declarations and declarations cut to fit a node keep the colors of the parts that are there.
    public static class ArchitectorDeclarationSyntax
    {
        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "abstract", "add", "async", "base", "bool", "byte", "char", "class", "const", "decimal", "default", "delegate",
            "double", "dynamic", "enum", "event", "explicit", "extern", "false", "float", "get", "implicit", "in", "init",
            "int", "interface", "internal", "long", "new", "null", "object", "operator", "out", "override", "params",
            "partial", "private", "protected", "public", "readonly", "record", "ref", "remove", "required", "sbyte",
            "sealed", "set", "short", "static", "string", "struct", "this", "true", "uint", "ulong", "unsafe", "ushort",
            "virtual", "void", "volatile", "where"
        };

        public static List<(string Text, ArchitectorCodeRole Role)> Classify(string declaration)
        {
            var tokens = Tokenize(declaration ?? string.Empty);
            var roles = new ArchitectorCodeRole[tokens.Count];
            for (var index = 0; index < tokens.Count; index++)
                roles[index] = InitialRole(tokens[index]);

            // The head runs to the parameter list ("(", or "[" after this), an accessor block or a value.
            var headEnd = tokens.Count;
            var angle = 0;
            var afterOperator = false;
            for (var index = 0; index < tokens.Count; index++)
            {
                var text = tokens[index];
                if (text == "operator")
                    afterOperator = true;
                if (afterOperator)
                {
                    // An operator symbol ("==", "<", "[]") is the name; only its parameter list ends the head.
                    if (text == "(")
                    {
                        headEnd = index;
                        break;
                    }
                    continue;
                }
                if (text == "<") angle++;
                else if (text == ">") angle = Math.Max(0, angle - 1);
                else if (angle == 0 && (text == "(" || text == "{" || text == "=" || text == "[" && PreviousWord(tokens, index) == "this"))
                {
                    headEnd = index;
                    break;
                }
            }

            var operatorAt = -1;
            for (var index = 0; index < headEnd; index++)
                if (tokens[index] == "operator")
                    operatorAt = index;
            if (operatorAt >= 0)
            {
                // "operator ==" and "implicit operator Int32": what follows operator names the member.
                for (var index = operatorAt + 1; index < headEnd; index++)
                    if (!IsSpace(tokens[index]))
                        roles[index] = ArchitectorCodeRole.Member;
            }
            else
            {
                angle = 0;
                var name = -1;
                for (var index = 0; index < headEnd; index++)
                {
                    var text = tokens[index];
                    if (text == "<") angle++;
                    else if (text == ">") angle = Math.Max(0, angle - 1);
                    else if (angle == 0 && (IsIdentifier(text) || text == "this"))
                        name = index;
                }
                if (name >= 0)
                    roles[name] = ArchitectorCodeRole.Member;
            }

            if (headEnd < tokens.Count && (tokens[headEnd] == "(" || tokens[headEnd] == "["))
                ClassifyParameters(tokens, roles, headEnd);
            else if (headEnd < tokens.Count && tokens[headEnd] == "=")
                for (var index = headEnd + 1; index < tokens.Count; index++)
                    if (roles[index] == ArchitectorCodeRole.Type)
                        roles[index] = ArchitectorCodeRole.Plain;

            var parts = new List<(string Text, ArchitectorCodeRole Role)>();
            for (var index = 0; index < tokens.Count; index++)
            {
                if (parts.Count > 0 && parts[parts.Count - 1].Role == roles[index] && (roles[index] == ArchitectorCodeRole.Plain || IsSpace(tokens[index])))
                {
                    parts[parts.Count - 1] = (parts[parts.Count - 1].Text + tokens[index], roles[index]);
                    continue;
                }
                parts.Add((tokens[index], IsSpace(tokens[index]) ? ArchitectorCodeRole.Plain : roles[index]));
            }
            return parts;
        }

        // In "(ref Vector3 point, Transform receiver = null)" the last name before a comma, the closing bracket or "="
        // is the parameter; the names before it are its type; what follows "=" is its default value.
        private static void ClassifyParameters(List<string> tokens, ArchitectorCodeRole[] roles, int open)
        {
            var depth = 0;
            var lastName = -1;
            var inDefault = false;
            var index = open;
            for (; index < tokens.Count; index++)
            {
                var text = tokens[index];
                if (text is "(" or "[" or "<")
                {
                    depth++;
                    if (index == open)
                        continue;
                }
                else if (text is ")" or "]" or ">")
                {
                    depth--;
                    if (depth == 0)
                        break;
                }
                var top = depth == 1;
                if (top && (text == "," || text == "="))
                {
                    if (lastName >= 0 && !inDefault)
                        roles[lastName] = ArchitectorCodeRole.Parameter;
                    lastName = -1;
                    inDefault = text == "=";
                    continue;
                }
                if (inDefault)
                {
                    if (roles[index] == ArchitectorCodeRole.Type)
                        roles[index] = ArchitectorCodeRole.Plain;
                    continue;
                }
                if (top && IsIdentifier(text))
                    lastName = index;
            }
            if (lastName >= 0 && !inDefault)
                roles[lastName] = ArchitectorCodeRole.Parameter;
            // After the list: accessors and constraints keep their keyword and type colors; names there are types.
        }

        private static ArchitectorCodeRole InitialRole(string token)
        {
            if (token.Length == 0 || IsSpace(token))
                return ArchitectorCodeRole.Plain;
            if (Keywords.Contains(token))
                return ArchitectorCodeRole.Keyword;
            if (token[0] == '"' || token[0] == '\'' || char.IsDigit(token[0]))
                return ArchitectorCodeRole.Literal;
            return IsIdentifier(token) ? ArchitectorCodeRole.Type : ArchitectorCodeRole.Plain;
        }

        private static string PreviousWord(List<string> tokens, int index)
        {
            for (var previous = index - 1; previous >= 0; previous--)
                if (!IsSpace(tokens[previous]))
                    return tokens[previous];
            return string.Empty;
        }

        private static bool IsIdentifier(string token)
        {
            return token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_' || token[0] == '@') && !Keywords.Contains(token);
        }

        private static bool IsSpace(string token) => token.Length > 0 && char.IsWhiteSpace(token[0]);

        private static List<string> Tokenize(string text)
        {
            var tokens = new List<string>();
            var index = 0;
            while (index < text.Length)
            {
                var start = index;
                var character = text[index];
                if (char.IsWhiteSpace(character))
                {
                    while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
                }
                else if (char.IsLetterOrDigit(character) || character == '_' || character == '@')
                {
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_' || text[index] == '@'
                                                   || char.IsDigit(character) && text[index] == '.')) index++;
                }
                else if (character == '"' || character == '\'')
                {
                    index++;
                    while (index < text.Length && text[index] != character)
                        index += text[index] == '\\' ? 2 : 1;
                    index = Math.Min(text.Length, index + 1);
                }
                else
                    index++;
                tokens.Add(text.Substring(start, index - start));
            }
            return tokens;
        }
    }
}
