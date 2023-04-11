# InfinityShaderCompiler
A Shaderlab like shader framework.

# Example Shader
Here is an example to use:

public class MinExample
{
    static void Main(string[] args)
    {
        Shaderlab shaderLab = ShaderlabUtility.ParseShaderlabFromFile("D:\\InfinityBrowser\\Shader\\InfinityLit.shader");
        Console.ReadKey();
    }
}

It will output 1 Catogory/Tags and 6 Pass/Tags and every string body inside HLSLPROGRAM:
