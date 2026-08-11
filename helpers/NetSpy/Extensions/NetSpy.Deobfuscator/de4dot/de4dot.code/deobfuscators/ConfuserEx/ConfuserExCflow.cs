/*
    NetSpy addition: expose ConfuserEx control-flow deobfuscation for a single method so the
    anti-tamper unpacker can clean the (control-flow + constant obfuscated) anti-tamper Initialize
    method before reading its injected seed constants. Without this, on the "maximum" preset the
    seed constants are computed through ConfuserEx's switch-based CFG and cannot be read statically.
*/

using de4dot.blocks;
using de4dot.blocks.cflow;
using dnlib.DotNet;

namespace de4dot.code.deobfuscators.ConfuserEx
{
    public static class ConfuserExCflow
    {
        /// <summary>
        /// Deobfuscates <paramref name="method"/>'s control flow in place using the ConfuserEx
        /// switch/CFG fixer plus generic constant folding, so obfuscated constant expressions
        /// collapse back to plain "ldc.i4 &lt;value&gt;; stloc" pairs. Best-effort: on any failure
        /// the method body is left unchanged.
        /// </summary>
        public static void Deobfuscate(MethodDef method)
        {
            if (method?.Body == null)
                return;
            try
            {
                var fixer = new ControlFlowFixer();
                // run a few passes: the CFG fixer linearizes the switch dispatch, then generic
                // constant folding resolves the arithmetic chains that encode the constants.
                for (int pass = 0; pass < 3; pass++)
                {
                    var blocks = new Blocks(method);
                    var cflow = new BlocksCflowDeobfuscator();
                    cflow.Initialize(blocks);
                    cflow.Add(fixer);
                    cflow.Deobfuscate();
                    blocks.RepartitionBlocks();
                    blocks.GetCode(out var instrs, out var ehs);
                    DotNetUtils.RestoreBody(method, instrs, ehs);
                }
            }
            catch
            {
                // best-effort; leave the body as-is on failure.
            }
        }
    }
}
