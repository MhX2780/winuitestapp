using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Controls;

namespace AdvaBrowser;

/// <summary>
/// Per-token syntax highlighter for RichEditBox.
/// Uses ITextRange + ITextCharacterFormat to color individual tokens.
/// Supports Python, JavaScript/TypeScript, C#, JSON, HTML, CSS, Bash, and more.
/// </summary>
public static class SyntaxHighlighter
{
    // ── Color palette (VS Code dark theme inspired) ──

    private static readonly Windows.UI.Color KeywordColor = Windows.UI.Color.FromArgb(255, 198, 120, 221);   // pink/purple — keywords
    private static readonly Windows.UI.Color ControlColor = Windows.UI.Color.FromArgb(255, 86, 156, 214);      // blue — control flow
    private static readonly Windows.UI.Color StringColor = Windows.UI.Color.FromArgb(255, 206, 145, 120);       // orange — strings
    private static readonly Windows.UI.Color CommentColor = Windows.UI.Color.FromArgb(255, 106, 153, 85);      // green — comments
    private static readonly Windows.UI.Color NumberColor = Windows.UI.Color.FromArgb(255, 181, 206, 168);       // light green — numbers
    private static readonly Windows.UI.Color TypeColor = Windows.UI.Color.FromArgb(255, 78, 201, 176);           // teal — types/classes
    private static readonly Windows.UI.Color FunctionColor = Windows.UI.Color.FromArgb(255, 220, 220, 170);     // pale yellow — function calls
    private static readonly Windows.UI.Color DecoratorColor = Windows.UI.Color.FromArgb(255, 188, 153, 255);     // lavender — decorators/attributes
    private static readonly Windows.UI.Color TagColor = Windows.UI.Color.FromArgb(255, 86, 156, 214);           // blue — HTML/XML tags
    private static readonly Windows.UI.Color AttrColor = Windows.UI.Color.FromArgb(255, 156, 220, 120);          // light green — HTML attributes
    private static readonly Windows.UI.Color PlainColor = Windows.UI.Color.FromArgb(255, 212, 212, 212);        // default text

    /// <summary>
    /// Applies syntax highlighting to a RichEditBox's TextDocument.
    /// </summary>
    public static void ApplyHighlighting(RichEditBox box, string language)
    {
        if (box == null) return;

        var doc = box.TextDocument;
        string text;
        doc.GetText(Microsoft.UI.Text.TextGetOptions.None, out text);
        if (string.IsNullOrEmpty(text)) return;

        var lang = (language ?? "").ToLowerInvariant();
        var tokens = Tokenize(text, lang);

        // Apply tokens in reverse order so positions stay valid
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            var token = tokens[i];
            try
            {
                var range = doc.GetRange(token.Start, token.End);
                if (range == null) continue;

                var fmt = range.CharacterFormat;
                if (fmt == null) continue;

                fmt.ForegroundColor = token.Color;
                fmt.Bold = token.Bold;
            }
            catch { }
        }
    }

    // ── Token representation ──

    private record SyntaxToken(int Start, int End, Windows.UI.Color Color, bool Bold = false);

    // ── Language keyword sets ──

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "def", "class", "if", "elif", "else", "for", "while", "try", "except", "finally",
        "with", "as", "import", "from", "return", "yield", "raise", "pass", "break",
        "continue", "assert", "del", "global", "nonlocal", "lambda", "async", "await",
        "in", "not", "and", "or", "is",
    };

    private static readonly HashSet<string> PythonBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "True", "False", "None", "self", "print", "len", "range", "str", "int", "float",
        "list", "dict", "set", "tuple", "bool", "type", "isinstance", "hasattr", "getattr",
        "setattr", "open", "input", "super", "staticmethod", "classmethod", "property",
        "enumerate", "zip", "map", "filter", "sorted", "reversed", "any", "all", "min", "max",
        "sum", "abs", "round", "format", "vars", "dir", "help", "id", "hash", "callable",
        "iter", "next", "exec", "eval", "compile", "repr", "chr", "ord", "hex", "oct", "bin",
    };

    private static readonly HashSet<string> JsKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "const", "let", "var", "function", "return", "if", "else", "for", "while", "do",
        "switch", "case", "break", "continue", "try", "catch", "finally", "throw", "new",
        "this", "class", "extends", "super", "import", "export", "default", "from", "of",
        "in", "typeof", "instanceof", "void", "delete", "yield", "async", "await",
        "static", "get", "set",
    };

    private static readonly HashSet<string> JsBuiltins = new(StringComparer.OrdinalIgnoreCase)
    {
        "true", "false", "null", "undefined", "NaN", "Infinity",
        "console", "document", "window", "Math", "JSON", "Object", "Array", "String",
        "Number", "Boolean", "Date", "RegExp", "Error", "Map", "Set", "Promise",
        "require", "module", "exports", "process", "global", "Buffer",
    };

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "using", "namespace", "class", "struct", "interface", "enum", "public", "private",
        "protected", "internal", "static", "readonly", "const", "new", "return", "if",
        "else", "for", "foreach", "while", "do", "switch", "case", "break", "continue",
        "try", "catch", "finally", "throw", "var", "dynamic", "object", "string", "int",
        "bool", "double", "float", "long", "decimal", "void", "async", "await", "yield",
        "in", "out", "ref", "is", "as", "when", "where", "get", "set", "init", "record",
        "abstract", "sealed", "virtual", "override", "partial", "this", "base", "null",
        "true", "false", "typeof", "nameof", "unsafe", "fixed", "extern",
    };

    private static readonly HashSet<string> CSharpTypes = new()
    {
        "Task", "List", "Dictionary", "HashSet", "Tuple", "ValueTuple", "Action", "Func",
        "EventHandler", "Exception", "string", "int", "bool", "double", "float", "long",
        "decimal", "object", "dynamic", "var", "void", "byte", "char", "short", "uint",
        "ulong", "ushort", "sbyte", "DateTime", "TimeSpan", "Guid", "Uri", "CancellationToken",
        "IEnumerable", "IQueryable", "ICollection", "IDisposable", "StringBuilder",
    };

    // ── Main tokenizer ──

    private static List<SyntaxToken> Tokenize(string text, string lang)
    {
        var tokens = new List<SyntaxToken>();

        switch (lang)
        {
            case "py" or "python":
                TokenizePython(text, tokens);
                break;
            case "js" or "javascript" or "jsx":
                TokenizeJs(text, tokens);
                break;
            case "ts" or "typescript" or "tsx":
                TokenizeJs(text, tokens); // JS tokenizer covers TS
                break;
            case "csharp" or "cs" or "c#":
                TokenizeCSharp(text, tokens);
                break;
            case "json":
                TokenizeJson(text, tokens);
                break;
            case "html" or "xml":
                TokenizeHtml(text, tokens);
                break;
            case "css" or "scss" or "less":
                TokenizeCss(text, tokens);
                break;
            case "bash" or "sh" or "shell" or "zsh" or "powershell" or "ps1":
                TokenizeBash(text, tokens);
                break;
            default:
                // Generic: highlight strings, comments, numbers
                TokenizeGeneric(text, tokens);
                break;
        }

        return tokens;
    }

    // ── Python tokenizer ──

    private static void TokenizePython(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Line comment: # ...
            if (text[i] == '#')
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Triple-quoted strings: """ or '''
            if (i + 2 < len && text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"')
            {
                int start = i;
                i += 3;
                while (i + 2 < len && !(text[i] == '"' && text[i + 1] == '"' && text[i + 2] == '"'))
                    i++;
                if (i + 2 < len) i += 3;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }
            if (i + 2 < len && text[i] == '\'' && text[i + 1] == '\'' && text[i + 2] == '\'')
            {
                int start = i;
                i += 3;
                while (i + 2 < len && !(text[i] == '\'' && text[i + 1] == '\'' && text[i + 2] == '\''))
                    i++;
                if (i + 2 < len) i += 3;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // f-strings and regular strings
            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                bool isF = i > 0 && text[i - 1] == 'f' || text[i - 1] == 'F';
                int start = i;
                i++;
                while (i < len && text[i] != quote && text[i] != '\n')
                {
                    if (text[i] == '\\') i++; // skip escaped char
                    i++;
                }
                if (i < len) i++; // closing quote
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Decorators: @something
            if (text[i] == '@' && (i == 0 || text[i - 1] == '\n'))
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '.'))
                    i++;
                tokens.Add(new SyntaxToken(start, i, DecoratorColor));
                continue;
            }

            // Numbers
            if (char.IsDigit(text[i]) || (text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                // hex: 0x...
                if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < len && IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    // exponent: 1e10, 1.5e-3
                    if (i < len && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < len && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < len && char.IsDigit(text[i])) i++;
                    }
                }
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            // Identifiers / keywords
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                string word = text[start..i];

                if (PythonKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, ControlColor, Bold: true));
                else if (PythonBuiltins.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                else if (IsCapsWord(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor)); // CLASS_NAMES
                // else: plain text (no token added)

                continue;
            }

            i++;
        }
    }

    // ── JavaScript/TypeScript tokenizer ──

    private static void TokenizeJs(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Line comment: // ...
            if (i + 1 < len && text[i] == '/' && text[i + 1] == '/')
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Block comment: /* ... */
            if (i + 1 < len && text[i] == '/' && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                if (i + 1 < len) i += 2;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Template literals: `...`
            if (text[i] == '`')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '`')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Strings: "..." or '...'
            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                int start = i;
                i++;
                while (i < len && text[i] != quote && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Numbers
            if (char.IsDigit(text[i]) || (text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < len && IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    if (i < len && (text[i] == 'e' || text[i] == 'E'))
                    {
                        i++;
                        if (i < len && (text[i] == '+' || text[i] == '-')) i++;
                        while (i < len && char.IsDigit(text[i])) i++;
                    }
                }
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            // Decorators / attributes: @decorator
            if (text[i] == '@')
            {
                int start = i;
                i++;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                tokens.Add(new SyntaxToken(start, i, DecoratorColor));
                continue;
            }

            // JSX/TSX: <Component ... />
            if (text[i] == '<' && i + 1 < len && char.IsUpper(text[i + 1]))
            {
                int start = i;
                while (i < len && text[i] != '>' && text[i] != '\n') i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, TypeColor));
                continue;
            }

            // Identifiers / keywords
            if (char.IsLetter(text[i]) || text[i] == '_' || text[i] == '$')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_' || text[i] == '$'))
                    i++;
                string word = text[start..i];

                if (JsKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, ControlColor, Bold: true));
                else if (JsBuiltins.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                else if (IsCapsWord(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                // Check if followed by ( → function call
                else if (i < len && text[i] == '(')
                    tokens.Add(new SyntaxToken(start, i, FunctionColor));

                continue;
            }

            i++;
        }
    }

    // ── C# tokenizer ──

    private static void TokenizeCSharp(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Line comment: // ...
            if (i + 1 < len && text[i] == '/' && text[i + 1] == '/')
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Block comment: /* ... */
            if (i + 1 < len && text[i] == '/' && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                if (i + 1 < len) i += 2;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Verbatim string: @"..." or $@"..."
            if (i + 1 < len && text[i] == '@' && text[i + 1] == '"')
            {
                int start = i;
                i += 2;
                while (i < len && text[i] != '"')
                {
                    if (text[i] == '"') i++; // escaped quote inside verbatim: ""
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Interpolated string: $"..." or $@"..."
            if (text[i] == '$' && i + 1 < len && text[i + 1] == '"')
            {
                int start = i;
                i += 2;
                while (i < len && text[i] != '"')
                {
                    if (text[i] == '\\') i++; // skip escape
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Regular strings
            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                int start = i;
                i++;
                while (i < len && text[i] != quote && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Preprocessor directives: #region, #if, etc.
            if (text[i] == '#' && (i == 0 || text[i - 1] == '\n'))
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, DecoratorColor));
                continue;
            }

            // Attributes: [Something]
            if (text[i] == '[' && i + 1 < len && char.IsLetter(text[i + 1]))
            {
                int start = i;
                while (i < len && text[i] != ']' && text[i] != '\n') i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, DecoratorColor));
                continue;
            }

            // Numbers
            if (char.IsDigit(text[i]) || (text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                if (text[i] == '0' && i + 1 < len && (text[i + 1] == 'x' || text[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < len && IsHexDigit(text[i])) i++;
                }
                else
                {
                    while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                    // suffix: f, d, m, etc.
                    if (i < len && (text[i] == 'f' || text[i] == 'F' || text[i] == 'd' || text[i] == 'D'
                        || text[i] == 'm' || text[i] == 'M' || text[i] == 'l' || text[i] == 'L'
                        || text[i] == 'u' || text[i] == 'U'))
                        i++;
                }
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            // Identifiers / keywords
            if (char.IsLetter(text[i]) || text[i] == '_')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                string word = text[start..i];

                if (CSharpKeywords.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, ControlColor, Bold: true));
                else if (CSharpTypes.Contains(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                else if (IsCapsWord(word))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                else if (i < len && text[i] == '(')
                    tokens.Add(new SyntaxToken(start, i, FunctionColor));

                continue;
            }

            i++;
        }
    }

    // ── JSON tokenizer ──

    private static void TokenizeJson(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            if (text[i] == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                // Keys (before ':') vs values: check context
                int j = i;
                while (j < len && char.IsWhiteSpace(text[j])) j++;
                if (j < len && text[j] == ':')
                    tokens.Add(new SyntaxToken(start, i, AttrColor)); // key
                else
                    tokens.Add(new SyntaxToken(start, i, StringColor)); // string value
                continue;
            }

            if (char.IsDigit(text[i]) || text[i] == '-')
            {
                int start = i;
                if (text[i] == '-') i++;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.' || text[i] == 'e' || text[i] == 'E' || text[i] == '+' || text[i] == '-'))
                    i++;
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            // true, false, null
            if (char.IsLetter(text[i]))
            {
                int start = i;
                while (i < len && char.IsLetter(text[i])) i++;
                string word = text[start..i];
                if (word is "true" or "false" or "null")
                    tokens.Add(new SyntaxToken(start, i, KeywordColor, Bold: true));
                continue;
            }

            i++;
        }
    }

    // ── HTML tokenizer ──

    private static void TokenizeHtml(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Comment: <!-- ... -->
            if (i + 3 < len && text[i] == '<' && text[i + 1] == '!' && text[i + 2] == '-' && text[i + 3] == '-')
            {
                int start = i;
                i += 4;
                while (i + 2 < len && !(text[i] == '-' && text[i + 1] == '-' && text[i + 2] == '>'))
                    i++;
                if (i + 2 < len) i += 3;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Tag: <tag ...> or </tag>
            if (text[i] == '<')
            {
                int start = i;
                i++;
                bool isClosing = i < len && text[i] == '/';
                if (isClosing) i++;
                // tag name
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == ':'))
                    i++;
                tokens.Add(new SyntaxToken(start, i, isClosing ? ControlColor : TagColor, Bold: true));

                // attributes inside tag
                while (i < len && text[i] != '>')
                {
                    // Skip whitespace
                    while (i < len && char.IsWhiteSpace(text[i])) i++;
                    if (i < len && text[i] == '>') break;

                    // Attribute name
                    if (char.IsLetter(text[i]))
                    {
                        int attrStart = i;
                        while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-' || text[i] == ':'))
                            i++;
                        tokens.Add(new SyntaxToken(attrStart, i, AttrColor));
                    }

                    // Skip to next attribute or '>'
                    while (i < len && text[i] != '>' && !char.IsLetter(text[i]) && text[i] != '\n')
                    {
                        if (text[i] == '"')
                        {
                            int sStart = i; i++;
                            while (i < len && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                            if (i < len) i++;
                            tokens.Add(new SyntaxToken(sStart, i, StringColor));
                        }
                        else
                        {
                            i++;
                        }
                    }
                }

                if (i < len) i++; // '>'
                continue;
            }

            // Strings outside tags
            if (text[i] == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"') { if (text[i] == '\\') i++; i++; }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            i++;
        }
    }

    // ── CSS tokenizer ──

    private static void TokenizeCss(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Comment
            if (i + 1 < len && text[i] == '/' && text[i + 1] == '*')
            {
                int start = i;
                i += 2;
                while (i + 1 < len && !(text[i] == '*' && text[i + 1] == '/'))
                    i++;
                if (i + 1 < len) i += 2;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // String
            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                int start = i;
                i++;
                while (i < len && text[i] != quote && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Properties (word followed by :)
            if (char.IsLetter(text[i]) || text[i] == '-')
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '-'))
                    i++;
                string word = text[start..i];
                // Check if property (followed by :)
                int j = i;
                while (j < len && char.IsWhiteSpace(text[j])) j++;
                if (j < len && text[j] == ':')
                    tokens.Add(new SyntaxToken(start, i, AttrColor));
                else if (word.StartsWith("@"))
                    tokens.Add(new SyntaxToken(start, i, DecoratorColor));
                else if (word.StartsWith(".") || word.StartsWith("#"))
                    tokens.Add(new SyntaxToken(start, i, TypeColor));
                // else: selector/keyword — plain
                continue;
            }

            // Numbers with units
            if (char.IsDigit(text[i]) || (text[i] == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                // unit: px, em, rem, %, etc.
                if (i < len && char.IsLetter(text[i]))
                {
                    while (i < len && char.IsLetter(text[i])) i++;
                }
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            // #color hex values
            if (text[i] == '#' && i + 1 < len && IsHexDigit(text[i + 1]))
            {
                int start = i;
                i++;
                while (i < len && IsHexDigit(text[i])) i++;
                if (i - start <= 9) // #RGB or #RRGGBB or #RRGGBBAA
                    tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            i++;
        }
    }

    // ── Bash/Shell tokenizer ──

    private static void TokenizeBash(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Comment
            if (text[i] == '#')
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Strings
            if (text[i] == '"')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '"' && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            if (text[i] == '\'')
            {
                int start = i;
                i++;
                while (i < len && text[i] != '\'' && text[i] != '\n') i++;
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Variables: $VAR or ${VAR}
            if (text[i] == '$')
            {
                int start = i;
                i++;
                if (i < len && text[i] == '{')
                {
                    i++;
                    while (i < len && text[i] != '}') i++;
                    if (i < len) i++;
                }
                else
                {
                    while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                }
                tokens.Add(new SyntaxToken(start, i, TypeColor));
                continue;
            }

            // Keywords
            if (char.IsLetter(text[i]))
            {
                int start = i;
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                    i++;
                string word = text[start..i];
                if (word is "if" or "then" or "else" or "fi" or "for" or "while" or "do" or "done"
                    or "case" or "esac" or "in" or "function" or "return" or "exit" or "local"
                    or "export" or "readonly" or "declare" or "unset" or "echo" or "set")
                    tokens.Add(new SyntaxToken(start, i, ControlColor, Bold: true));
                continue;
            }

            i++;
        }
    }

    // ── Generic tokenizer (fallback) ──

    private static void TokenizeGeneric(string text, List<SyntaxToken> tokens)
    {
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Line comment: # or //
            if ((text[i] == '#' && (i == 0 || text[i - 1] == '\n'))
                || (i + 1 < len && text[i] == '/' && text[i + 1] == '/'))
            {
                int start = i;
                while (i < len && text[i] != '\n') i++;
                tokens.Add(new SyntaxToken(start, i, CommentColor));
                continue;
            }

            // Strings
            if (text[i] is '"' or '\'')
            {
                char quote = text[i];
                int start = i;
                i++;
                while (i < len && text[i] != quote && text[i] != '\n')
                {
                    if (text[i] == '\\') i++;
                    i++;
                }
                if (i < len) i++;
                tokens.Add(new SyntaxToken(start, i, StringColor));
                continue;
            }

            // Numbers
            if (char.IsDigit(text[i]))
            {
                int start = i;
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.')) i++;
                tokens.Add(new SyntaxToken(start, i, NumberColor));
                continue;
            }

            i++;
        }
    }

    // ── Helpers ──

    private static bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>
    /// Returns true if word looks like a class/type name: PascalCase with no lowercase start.
    /// </summary>
    private static bool IsCapsWord(string word) =>
        word.Length > 1 && char.IsUpper(word[0]) && !word.Contains('_')
        && word.Any(char.IsLower) && !word.All(char.IsUpper);
}
