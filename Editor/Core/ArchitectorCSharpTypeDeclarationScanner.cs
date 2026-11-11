using System;
using System.Collections.Generic;
using System.IO;

namespace Architector.Editor
{
    internal sealed class ArchitectorCSharpTypeDeclaration
    {
        public string MetadataName { get; set; } = string.Empty;
        public int Line { get; set; }
        public int Column { get; set; }
    }

    internal static class ArchitectorCSharpTypeDeclarationScanner
    {
        private static readonly Dictionary<string, FileCacheEntry> FileCache =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly object FileCacheLock = new();

        // Safe to call from worker threads: the type index scans files in parallel.
        public static IReadOnlyList<ArchitectorCSharpTypeDeclaration> ScanFile(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var info = new FileInfo(fullPath);
            lock (FileCacheLock)
            {
                if (FileCache.TryGetValue(fullPath, out var cached)
                    && cached.Length == info.Length
                    && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
                    return cached.Declarations;
            }
            var declarations = Scan(File.ReadAllText(fullPath));
            lock (FileCacheLock)
                FileCache[fullPath] = new FileCacheEntry(info.Length, info.LastWriteTimeUtc, declarations);
            return declarations;
        }

        public static IReadOnlyList<ArchitectorCSharpTypeDeclaration> Scan(string source)
        {
            var tokens = Tokenize(source ?? string.Empty);
            var declarations = new List<ArchitectorCSharpTypeDeclaration>();
            var index = 0;
            ParseScope(tokens, ref index, string.Empty, Array.Empty<string>(), false, declarations);
            return declarations;
        }

        private static void ParseScope(
            IReadOnlyList<Token> tokens,
            ref int index,
            string namespaceName,
            IReadOnlyList<string> enclosingTypes,
            bool stopAtCloseBrace,
            ICollection<ArchitectorCSharpTypeDeclaration> declarations)
        {
            var currentNamespace = namespaceName;
            while (index < tokens.Count)
            {
                if (tokens[index].Text == "}")
                {
                    index++;
                    if (stopAtCloseBrace)
                        return;
                    continue;
                }

                if (enclosingTypes.Count == 0 && tokens[index].Text == "namespace"
                    && TryParseNamespace(tokens, ref index, currentNamespace, enclosingTypes, declarations, out var fileNamespace))
                {
                    if (fileNamespace != null)
                        currentNamespace = fileNamespace;
                    continue;
                }

                if (TryParseType(tokens, ref index, currentNamespace, enclosingTypes, declarations))
                    continue;

                if (tokens[index].Text == "{")
                {
                    SkipBalancedBlock(tokens, ref index);
                    continue;
                }
                index++;
            }
        }

        private static bool TryParseNamespace(
            IReadOnlyList<Token> tokens,
            ref int index,
            string currentNamespace,
            IReadOnlyList<string> enclosingTypes,
            ICollection<ArchitectorCSharpTypeDeclaration> declarations,
            out string fileNamespace)
        {
            fileNamespace = null;
            var cursor = index + 1;
            var parts = new List<string>();
            var expectIdentifier = true;
            while (cursor < tokens.Count)
            {
                var token = tokens[cursor];
                if (expectIdentifier)
                {
                    if (!token.IsIdentifier)
                        return false;
                    parts.Add(token.Text);
                    expectIdentifier = false;
                    cursor++;
                    continue;
                }
                if (token.Text == ".")
                {
                    expectIdentifier = true;
                    cursor++;
                    continue;
                }
                break;
            }
            if (parts.Count == 0 || expectIdentifier || cursor >= tokens.Count)
                return false;
            var declaredNamespace = CombineNamespace(currentNamespace, string.Join(".", parts));
            if (tokens[cursor].Text == ";")
            {
                index = cursor + 1;
                fileNamespace = declaredNamespace;
                return true;
            }
            if (tokens[cursor].Text != "{")
                return false;
            index = cursor + 1;
            ParseScope(tokens, ref index, declaredNamespace, enclosingTypes, true, declarations);
            return true;
        }

        private static bool TryParseType(
            IReadOnlyList<Token> tokens,
            ref int index,
            string namespaceName,
            IReadOnlyList<string> enclosingTypes,
            ICollection<ArchitectorCSharpTypeDeclaration> declarations)
        {
            var start = index;
            var keyword = tokens[start].Text;
            if (keyword == "delegate")
                return TryParseDelegate(tokens, ref index, namespaceName, enclosingTypes, declarations);
            if (keyword != "class" && keyword != "struct" && keyword != "interface"
                && keyword != "enum" && keyword != "record")
                return false;

            var nameIndex = start + 1;
            if (keyword == "record" && nameIndex < tokens.Count
                && (tokens[nameIndex].Text == "class" || tokens[nameIndex].Text == "struct"))
                nameIndex++;
            if (nameIndex >= tokens.Count || !tokens[nameIndex].IsIdentifier)
                return false;

            var cursor = nameIndex + 1;
            var metadataSegment = tokens[nameIndex].Text + ParseGenericArity(tokens, ref cursor);
            var terminator = FindTypeTerminator(tokens, cursor);
            if (terminator < 0)
                return false;

            declarations.Add(CreateDeclaration(tokens[nameIndex], namespaceName, enclosingTypes, metadataSegment));
            if (tokens[terminator].Text == ";")
            {
                index = terminator + 1;
                return true;
            }

            var nested = new List<string>(enclosingTypes) { metadataSegment };
            index = terminator + 1;
            ParseScope(tokens, ref index, namespaceName, nested, true, declarations);
            return true;
        }

        private static bool TryParseDelegate(
            IReadOnlyList<Token> tokens,
            ref int index,
            string namespaceName,
            IReadOnlyList<string> enclosingTypes,
            ICollection<ArchitectorCSharpTypeDeclaration> declarations)
        {
            var cursor = index + 1;
            var angleDepth = 0;
            var nameIndex = -1;
            while (cursor < tokens.Count)
            {
                var text = tokens[cursor].Text;
                if (text == "<") angleDepth++;
                else if (text == ">" && angleDepth > 0) angleDepth--;
                else if (text == "(" && angleDepth == 0) break;
                else if (text == ";" || text == "{") return false;
                else if (tokens[cursor].IsIdentifier && angleDepth == 0) nameIndex = cursor;
                cursor++;
            }
            if (nameIndex < 0 || cursor >= tokens.Count || tokens[cursor].Text != "(")
                return false;
            var afterName = nameIndex + 1;
            var metadataSegment = tokens[nameIndex].Text + ParseGenericArity(tokens, ref afterName);
            while (cursor < tokens.Count && tokens[cursor].Text != ";")
                cursor++;
            if (cursor >= tokens.Count)
                return false;
            declarations.Add(CreateDeclaration(tokens[nameIndex], namespaceName, enclosingTypes, metadataSegment));
            index = cursor + 1;
            return true;
        }

        private static ArchitectorCSharpTypeDeclaration CreateDeclaration(
            Token nameToken,
            string namespaceName,
            IReadOnlyList<string> enclosingTypes,
            string metadataSegment)
        {
            var typeName = enclosingTypes.Count == 0
                ? metadataSegment
                : string.Join("+", enclosingTypes) + "+" + metadataSegment;
            return new ArchitectorCSharpTypeDeclaration
            {
                MetadataName = string.IsNullOrEmpty(namespaceName) ? typeName : namespaceName + "." + typeName,
                Line = nameToken.Line,
                Column = nameToken.Column
            };
        }

        private static string ParseGenericArity(IReadOnlyList<Token> tokens, ref int cursor)
        {
            if (cursor >= tokens.Count || tokens[cursor].Text != "<")
                return string.Empty;
            var depth = 0;
            var arity = 1;
            while (cursor < tokens.Count)
            {
                var text = tokens[cursor++].Text;
                if (text == "<") depth++;
                else if (text == ">")
                {
                    depth--;
                    if (depth == 0)
                        return "`" + arity;
                }
                else if (text == "," && depth == 1)
                {
                    arity++;
                }
            }
            return string.Empty;
        }

        private static int FindTypeTerminator(IReadOnlyList<Token> tokens, int cursor)
        {
            var parentheses = 0;
            var brackets = 0;
            for (; cursor < tokens.Count; cursor++)
            {
                var text = tokens[cursor].Text;
                if (text == "(") parentheses++;
                else if (text == ")" && parentheses > 0) parentheses--;
                else if (text == "[") brackets++;
                else if (text == "]" && brackets > 0) brackets--;
                else if (parentheses == 0 && brackets == 0 && (text == "{" || text == ";")) return cursor;
            }
            return -1;
        }

        private static void SkipBalancedBlock(IReadOnlyList<Token> tokens, ref int index)
        {
            var depth = 0;
            while (index < tokens.Count)
            {
                if (tokens[index].Text == "{") depth++;
                else if (tokens[index].Text == "}" && --depth == 0)
                {
                    index++;
                    return;
                }
                index++;
            }
        }

        private static string CombineNamespace(string current, string declared) =>
            string.IsNullOrEmpty(current) ? declared : current + "." + declared;

        private static List<Token> Tokenize(string source)
        {
            var result = new List<Token>();
            var index = 0;
            var line = 1;
            var column = 1;
            while (index < source.Length)
            {
                var character = source[index];
                if (char.IsWhiteSpace(character))
                {
                    Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (character == '/' && Peek(source, index + 1) == '/')
                {
                    while (index < source.Length && source[index] != '\n')
                        Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (character == '/' && Peek(source, index + 1) == '*')
                {
                    Advance(source, ref index, ref line, ref column);
                    Advance(source, ref index, ref line, ref column);
                    while (index < source.Length && !(source[index] == '*' && Peek(source, index + 1) == '/'))
                        Advance(source, ref index, ref line, ref column);
                    if (index < source.Length)
                    {
                        Advance(source, ref index, ref line, ref column);
                        Advance(source, ref index, ref line, ref column);
                    }
                    continue;
                }
                if (character == '#')
                {
                    while (index < source.Length && source[index] != '\n')
                        Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (TrySkipLiteral(source, ref index, ref line, ref column))
                    continue;
                if (IsIdentifierStart(character) || character == '@' && IsIdentifierStart(Peek(source, index + 1)))
                {
                    var tokenLine = line;
                    var tokenColumn = column;
                    if (character == '@')
                        Advance(source, ref index, ref line, ref column);
                    var start = index;
                    while (index < source.Length && IsIdentifierPart(source[index]))
                        Advance(source, ref index, ref line, ref column);
                    result.Add(new Token(source.Substring(start, index - start), tokenLine, tokenColumn, true));
                    continue;
                }
                result.Add(new Token(character.ToString(), line, column, false));
                Advance(source, ref index, ref line, ref column);
            }
            return result;
        }

        private static bool TrySkipLiteral(string source, ref int index, ref int line, ref int column)
        {
            var cursor = index;
            var verbatim = false;
            var interpolated = false;
            if (Peek(source, cursor) == '$')
            {
                interpolated = true;
                cursor++;
            }
            if (Peek(source, cursor) == '@')
            {
                verbatim = true;
                cursor++;
                if (Peek(source, cursor) == '$')
                {
                    interpolated = true;
                    cursor++;
                }
            }
            if (Peek(source, cursor) != '"' && Peek(source, cursor) != '\'')
                return false;
            var quote = source[cursor];
            var quoteCount = 1;
            while (Peek(source, cursor + quoteCount) == quote)
                quoteCount++;
            if (quoteCount >= 3)
            {
                while (index < cursor + quoteCount)
                    Advance(source, ref index, ref line, ref column);
                while (index < source.Length)
                {
                    var matches = true;
                    for (var offset = 0; offset < quoteCount; offset++)
                        matches &= Peek(source, index + offset) == quote;
                    if (matches)
                    {
                        for (var offset = 0; offset < quoteCount; offset++)
                            Advance(source, ref index, ref line, ref column);
                        return true;
                    }
                    Advance(source, ref index, ref line, ref column);
                }
                return true;
            }

            while (index <= cursor)
                Advance(source, ref index, ref line, ref column);
            var interpolationDepth = 0;
            while (index < source.Length)
            {
                if (interpolationDepth > 0)
                {
                    if (TrySkipLiteral(source, ref index, ref line, ref column))
                        continue;
                    if (source[index] == '{')
                    {
                        interpolationDepth++;
                        Advance(source, ref index, ref line, ref column);
                        continue;
                    }
                    if (source[index] == '}')
                    {
                        interpolationDepth--;
                        Advance(source, ref index, ref line, ref column);
                        continue;
                    }
                    Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (interpolated && source[index] == '{')
                {
                    Advance(source, ref index, ref line, ref column);
                    if (Peek(source, index) == '{')
                        Advance(source, ref index, ref line, ref column);
                    else
                        interpolationDepth = 1;
                    continue;
                }
                if (interpolated && source[index] == '}' && Peek(source, index + 1) == '}')
                {
                    Advance(source, ref index, ref line, ref column);
                    Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (!verbatim && source[index] == '\\')
                {
                    Advance(source, ref index, ref line, ref column);
                    if (index < source.Length)
                        Advance(source, ref index, ref line, ref column);
                    continue;
                }
                if (source[index] == quote)
                {
                    Advance(source, ref index, ref line, ref column);
                    if (verbatim && quote == '"' && Peek(source, index) == '"')
                    {
                        Advance(source, ref index, ref line, ref column);
                        continue;
                    }
                    return true;
                }
                Advance(source, ref index, ref line, ref column);
            }
            return true;
        }

        private sealed class FileCacheEntry
        {
            public FileCacheEntry(
                long length,
                DateTime lastWriteTimeUtc,
                IReadOnlyList<ArchitectorCSharpTypeDeclaration> declarations)
            {
                Length = length;
                LastWriteTimeUtc = lastWriteTimeUtc;
                Declarations = declarations;
            }

            public long Length { get; }
            public DateTime LastWriteTimeUtc { get; }
            public IReadOnlyList<ArchitectorCSharpTypeDeclaration> Declarations { get; }
        }

        private static void Advance(string source, ref int index, ref int line, ref int column)
        {
            if (source[index++] == '\n')
            {
                line++;
                column = 1;
            }
            else
            {
                column++;
            }
        }

        private static char Peek(string source, int index) =>
            index >= 0 && index < source.Length ? source[index] : '\0';

        private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
        private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);

        private readonly struct Token
        {
            public Token(string text, int line, int column, bool isIdentifier)
            {
                Text = text;
                Line = line;
                Column = column;
                IsIdentifier = isIdentifier;
            }

            public string Text { get; }
            public int Line { get; }
            public int Column { get; }
            public bool IsIdentifier { get; }
        }
    }
}
