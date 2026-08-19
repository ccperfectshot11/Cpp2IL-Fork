using System;

namespace Cpp2IL.Pipeline
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            Console.WriteLine("=== Cpp2IL Pipeline ===");
            Console.WriteLine("Stage 1 (Cpp2IL): " + typeof(global::Cpp2IL.Core.Cpp2IlApi).FullName);
            Console.WriteLine("Stage 2 (NetSpy):  " + typeof(global::ICSharpCode.Decompiler.Ast.AstBuilder).FullName);
            Console.WriteLine("Coexistence OK.");
        }
    }
}
