using Antlr4.Runtime;

namespace SharpShader.ShaderLab.Frontend
{
    internal sealed class ShaderLabParserErrorListener : BaseErrorListener
    {
        public static readonly ShaderLabParserErrorListener Instance = new ShaderLabParserErrorListener();

        private ShaderLabParserErrorListener()
        {
        }

        public override void SyntaxError(
            System.IO.TextWriter output,
            IRecognizer recognizer,
            IToken offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            throw new ShaderLabParseException(line, charPositionInLine + 1, $"ShaderLab 语法错误：{msg}");
        }
    }

    internal sealed class ShaderLabLexerErrorListener : IAntlrErrorListener<int>
    {
        public static readonly ShaderLabLexerErrorListener Instance = new ShaderLabLexerErrorListener();

        private ShaderLabLexerErrorListener()
        {
        }

        public void SyntaxError(
            System.IO.TextWriter output,
            IRecognizer recognizer,
            int offendingSymbol,
            int line,
            int charPositionInLine,
            string msg,
            RecognitionException e)
        {
            throw new ShaderLabParseException(line, charPositionInLine + 1, $"ShaderLab 词法错误：{msg}");
        }
    }
}
