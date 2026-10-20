using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Pcl299Unlock
{
    internal sealed class PatchResult
    {
        public string Id;
        public string Target;
        public string Strategy;
        public string Status;
        public string Detail;
        public List<string> AddedMembers = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    internal static class Program
    {
        private const string ExpectedSha256 = "6B6E1260CB5A1D6A303855F1C60CAB3D0BC1AEBBA7E40A964180188B271E7386";
        private const string ExpectedDonorSha256 = "5582952DED3B443503F66C23B810802BD20F1A3E847B54D9E09653A825859A36";
        private const string GateMessage = "为遵守相关法律法规，PCL 暂时不再提供 MC 下载功能，非常抱歉 qwq……";
        private const string ExpectedAssembly = "Plain Craft Launcher 2, Version=2.99.0.0";

        private static int Main(string[] args)
        {
            try { return Run(args); }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FATAL: " + ex.GetType().FullName + ": " + ex.Message);
                if (ex.InnerException != null) Console.Error.WriteLine("INNER: " + ex.InnerException.Message);
                Console.Error.WriteLine(ex.StackTrace);
                return 3;
            }
        }

        private static int Run(string[] args)
        {
            string input = null, output = null, report = null, donor = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a == "--input" && i + 1 < args.Length) input = args[++i];
                else if (a == "--output" && i + 1 < args.Length) output = args[++i];
                else if (a == "--report" && i + 1 < args.Length) report = args[++i];
                else if (a == "--donor" && i + 1 < args.Length) donor = args[++i];
                else if (a == "--help" || a == "-h")
                {
                    Console.WriteLine("pcl299-unlock --input <exe> --output <exe> [--report <json>] [--donor <2.14 exe>]");
                    return 0;
                }
                else { Console.Error.WriteLine("unknown argument: " + a); return 2; }
            }
            if (input == null) { Console.Error.WriteLine("--input is required"); return 2; }
            if (output == null) output = Path.Combine(Environment.CurrentDirectory, "Plain Craft Launcher 2.patched.exe");
            if (report == null) report = Path.Combine(Environment.CurrentDirectory, "patch-report.json");
            if (donor == null) { Console.Error.WriteLine("--donor is required: pass a local PCL 2.14.0.0 executable that you obtained yourself."); return 2; }
            input = Path.GetFullPath(input);
            output = Path.GetFullPath(output);
            report = Path.GetFullPath(report);
            donor = Path.GetFullPath(donor);

            string actual = Sha256(input);
            if (!string.Equals(actual, ExpectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("REFUSED: input SHA256 is " + actual + ", expected " + ExpectedSha256 + " (PCL 2.99.0.0).");
                return 4;
            }
            if (!File.Exists(donor)) { Console.Error.WriteLine("REFUSED: donor not found: " + donor); return 5; }
            string donorHash = Sha256(donor);
            if (!string.Equals(donorHash, ExpectedDonorSha256, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("REFUSED: donor SHA256 is " + donorHash + ", expected " + ExpectedDonorSha256 + " (PCL 2.14.0.0 official).");
                return 5;
            }
            if (string.Equals(input, output, StringComparison.OrdinalIgnoreCase)) { Console.Error.WriteLine("REFUSED: refusing to overwrite input"); return 6; }

            var results = new List<PatchResult>();
            var mod = ModuleDefMD.Load(input);
            if (!mod.Assembly.FullName.StartsWith(ExpectedAssembly, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("REFUSED: unexpected assembly: " + mod.Assembly.FullName);
                return 7;
            }
            var donorMod = ModuleDefMD.Load(donor);

            PatchCardVisibility(mod, results);
            PatchLoaderInitInline(mod, results);
            ReplaceMethodBody(mod, donorMod, results, "modpack-entry", "PCL.ModModpack", "ModpackInstall", 0);
            ReplaceMethodBody(mod, donorMod, results, "modpack-core", "PCL.ModModpack", "ModpackInstall", 3);
            ReplaceMethodBody(mod, donorMod, results, "resource-install", "PCL.PageDownloadResourceDetail", "InitializeStackResourceVersions", 1);
            PatchDragInstall(mod, results);
            ClearDeadClosure(mod, results);

            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var options = new ModuleWriterOptions(mod);
            options.Logger = DummyLogger.ThrowModuleWriterExceptionOnErrorInstance;
            mod.Write(output, options);

            var reportData = new Dictionary<string, object>
            {
                { "tool", "pcl299-unlock" },
                { "input", new Dictionary<string, object> { { "path", input }, { "sha256", actual }, { "assembly", ExpectedAssembly } } },
                { "output", new Dictionary<string, object> { { "path", output }, { "sha256", Sha256(output) }, { "size", new FileInfo(output).Length } } },
                { "result", results.All(r => r.Status == "applied") ? "applied" : "partial" },
                { "patches", results.Select(r => new Dictionary<string, object>
                    {
                        { "id", r.Id }, { "target", r.Target }, { "strategy", r.Strategy },
                        { "status", r.Status }, { "detail", r.Detail },
                        { "added_members", r.AddedMembers }, { "warnings", r.Warnings }
                    }).ToList() }
            };
            File.WriteAllText(report, SimpleJson.Serialize(reportData), new UTF8Encoding(false));

            foreach (var r in results) Console.WriteLine((r.Status == "applied" ? "[OK]   " : "[FAIL] ") + r.Id + " -> " + r.Target + " (" + r.Strategy + ") " + r.Detail);
            Console.WriteLine("output: " + output);
            Console.WriteLine("sha256: " + Sha256(output));
            Console.WriteLine("report: " + report);
            return results.All(r => r.Status == "applied") ? 0 : 8;
        }

        private static void PatchCardVisibility(ModuleDefMD mod, List<PatchResult> results)
        {
            var r = Start("modify-card", "PCL.PageDownloadModify::LoadInstanceFinished");
            try
            {
                var method = FindSingleMethod(mod, "PCL.PageDownloadModify", "LoadInstanceFinished");
                var ins = method.Body.Instructions;
                int idx = IndexOf(ins, x => x.OpCode == OpCodes.Callvirt && IsNamedMethod(x.Operand, "get_CardMinecraft"));
                if (idx < 0 || idx + 2 >= ins.Count) throw new InvalidOperationException("get_CardMinecraft not found");
                if (ins[idx + 1].OpCode != OpCodes.Ldc_I4_2) throw new InvalidOperationException("unexpected Visibility constant");
                ins[idx + 1].OpCode = OpCodes.Ldc_I4_0;
                ins[idx + 1].Operand = null;
                Done(r, "in-place IL edit", "CardMinecraft.Visibility: Collapsed -> Visible", results);
            }
            catch (Exception ex) { Fail(r, ex, results); }
        }

        private static void PatchLoaderInitInline(ModuleDefMD mod, List<PatchResult> results)
        {
            var r = Start("download-page", "PCL.PageDownloadInstall::LoaderInit");
            try
            {
                var method = FindSingleMethod(mod, "PCL.PageDownloadInstall", "LoaderInit");
                var field = mod.Find("PCL.ModDownload", false).Fields.Single(x => x.Name == "DlClientListLoader");
                var ins = method.Body.Instructions;

                int msg = IndexOf(ins, x => x.OpCode == OpCodes.Ldstr && (x.Operand as string) == "关闭下载");
                if (msg < 0) throw new InvalidOperationException("guard loader title not found");

                int ctor = -1;
                for (int i = msg; i < ins.Count; i++)
                {
                    var m = ins[i].Operand as IMethod;
                    if (ins[i].OpCode == OpCodes.Newobj && m != null && m.Name == ".ctor" && m.DeclaringType != null && m.DeclaringType.Name.StartsWith("LoaderTask"))
                    {
                        ctor = i;
                        break;
                    }
                }
                if (ctor < 0) throw new InvalidOperationException("LoaderTask constructor not found");

                ins[msg].OpCode = OpCodes.Ldsfld;
                ins[msg].Operand = field;
                for (int i = msg + 1; i <= ctor; i++) { ins[i].OpCode = OpCodes.Nop; ins[i].Operand = null; }

                Done(r, "in-place IL edit", "closed-download LoaderTask -> ldsfld ModDownload.DlClientListLoader (2.99 OnFinish callback preserved)", results);
            }
            catch (Exception ex) { Fail(r, ex, results); }
        }
        private static void PatchDragInstall(ModuleDefMD mod, List<PatchResult> results)
        {
            var r = Start("drag-drop", "PCL.FormMain/_Closure$__25-0::_Lambda$__0");
            try
            {
                var method = FindSingleMethod(mod, "PCL.FormMain/_Closure$__25-0", "_Lambda$__0");
                var ins = method.Body.Instructions;
                int msg = IndexOf(ins, x => x.OpCode == OpCodes.Ldstr && (x.Operand as string) == GateMessage);
                if (msg < 0) throw new InvalidOperationException("gate message not found");
                int cmp = IndexOfFrom(ins, msg, x => x.OpCode == OpCodes.Call && IsNamedMethod(x.Operand, "CompareString"));
                if (cmp < 0) throw new InvalidOperationException("CompareString not found");
                int branch = cmp + 1;
                if (ins[branch].OpCode != OpCodes.Brtrue_S && ins[branch].OpCode != OpCodes.Brtrue) throw new InvalidOperationException("unexpected branch");
                var afterGate = ins[branch].Operand as Instruction;
                if (afterGate == null) throw new InvalidOperationException("branch target missing");
                int start = msg - 2;
                if (start < 0) throw new InvalidOperationException("gate prologue missing");
                int end = ins.IndexOf(afterGate);
                for (int i = start; i < end; i++) { ins[i].OpCode = OpCodes.Nop; ins[i].Operand = null; }
                Done(r, "in-place IL edit", "removed RestrictMcDownload drag-drop early return", results);
            }
            catch (Exception ex) { Fail(r, ex, results); }
        }

        private static void ClearDeadClosure(ModuleDefMD mod, List<PatchResult> results)
        {
            var r = Start("dead-closure", "PCL.PageDownloadInstall/_Closure$__::_Lambda$__3-0");
            try
            {
                var t = mod.Find("PCL.PageDownloadInstall/_Closure$__", false);
                if (t == null) throw new InvalidOperationException("closure type not found");
                var m = t.Methods.SingleOrDefault(x => x.Name == "_Lambda$__3-0" && x.HasBody);
                if (m == null) { Done(r, "no-op", "no dead closure present", results); return; }
                var ins = m.Body.Instructions;
                if (ins.Count != 3 || ins[0].OpCode != OpCodes.Ldstr || ins[1].OpCode != OpCodes.Newobj || ins[2].OpCode != OpCodes.Throw)
                    throw new InvalidOperationException("unexpected dead closure body");
                ins.Clear();
                ins.Add(Instruction.Create(OpCodes.Ret));
                Done(r, "in-place IL edit", "neutralized unreferenced closed-download helper", results);
            }
            catch (Exception ex) { Fail(r, ex, results); }
        }

        private static void ReplaceMethodBody(ModuleDefMD mod, ModuleDefMD donor, List<PatchResult> results, string id, string typeName, string methodName, int parameterCount)
        {
            var r = Start(id, typeName + "::" + methodName + "/" + parameterCount);
            try
            {
                var targetType = mod.Find(typeName, false);
                var donorType = donor.Find(typeName, false);
                if (targetType == null) throw new InvalidOperationException("target type missing: " + typeName);
                if (donorType == null) throw new InvalidOperationException("donor type missing: " + typeName);
                var target = targetType.Methods.Single(x => x.Name == methodName && x.MethodSig.Params.Count == parameterCount && x.HasBody);
                var source = donorType.Methods.Single(x => x.Name == methodName && x.MethodSig.Params.Count == parameterCount && x.HasBody);
                if (target.MethodSig.ToString() != source.MethodSig.ToString()) throw new InvalidOperationException("signature mismatch");
                if (target.Body.Variables.Count != source.Body.Variables.Count) throw new InvalidOperationException("locals count mismatch");
                for (int i = 0; i < target.Body.Variables.Count; i++)
                    if (target.Body.Variables[i].Type.FullName != source.Body.Variables[i].Type.FullName)
                        throw new InvalidOperationException("locals mismatch at " + i);

                CloneBodyInto(target, source, mod);
                Done(r, "donor 2.14 method-body transplant", "signature and locals verified; body replaced", results);
            }
            catch (Exception ex) { Fail(r, ex, results); }
        }

        private static void CloneBodyInto(MethodDef target, MethodDef donor, ModuleDefMD targetModule)
        {
            var body = new CilBody();
            foreach (var v in donor.Body.Variables) body.Variables.Add(new Local(targetModule.Import(v.Type)));
            body.InitLocals = donor.Body.InitLocals;
            body.MaxStack = donor.Body.MaxStack;

            var map = new Dictionary<Instruction, Instruction>();
            foreach (var i in donor.Body.Instructions) map[i] = Instruction.Create(OpCodes.Nop);

            foreach (var i in donor.Body.Instructions)
            {
                var ni = map[i];
                ni.OpCode = i.OpCode;
                ni.Operand = RemapOperand(i.Operand, map, targetModule);
                body.Instructions.Add(ni);
            }

            foreach (var eh in donor.Body.ExceptionHandlers)
            {
                body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType)
                {
                    TryStart = eh.TryStart == null ? null : map[eh.TryStart],
                    TryEnd = eh.TryEnd == null ? null : map[eh.TryEnd],
                    HandlerStart = eh.HandlerStart == null ? null : map[eh.HandlerStart],
                    HandlerEnd = eh.HandlerEnd == null ? null : map[eh.HandlerEnd],
                    FilterStart = eh.FilterStart == null ? null : map[eh.FilterStart],
                    CatchType = eh.CatchType == null ? null : (ITypeDefOrRef)targetModule.Import((ITypeDefOrRef)eh.CatchType)
                });
            }
            target.Body = body;
        }

        private static object RemapOperand(object operand, Dictionary<Instruction, Instruction> map, ModuleDefMD targetModule)
        {
            var target = operand as Instruction;
            if (target != null) return map[target];
            var targets = operand as Instruction[];
            if (targets != null) return targets.Select(x => map[x]).ToArray();
            var method = operand as IMethod;
            if (method != null) return ImportMethod(method, targetModule);
            var field = operand as IField;
            if (field != null) return ImportField(field, targetModule);
            var type = operand as ITypeDefOrRef;
            if (type != null) return targetModule.Import(type);
            return operand;
        }

        private static IMethod ImportMethod(IMethod method, ModuleDefMD targetModule)
        {
            var spec = method as MethodSpec;
            if (spec != null)
            {
                var generic = ImportMethod(spec.Method, targetModule) as IMethodDefOrRef;
                var args = new List<TypeSig>();
                foreach (var a in spec.GenericInstMethodSig.GenericArguments) args.Add(targetModule.Import(a));
                if (generic != null) return new MethodSpecUser(generic, new GenericInstMethodSig(args));
            }
            if (method.DeclaringType != null && method.DeclaringType.DefinitionAssembly == targetModule.Assembly)
            {
                var resolved = targetModule.Find(method.DeclaringType.FullName, false);
                if (resolved != null)
                {
                    var hits = resolved.Methods.Where(x => x.Name == method.Name && x.MethodSig.ToString() == method.MethodSig.ToString()).ToList();
                    if (hits.Count == 1) return hits[0];
                }
            }
            return targetModule.Import(method);
        }

        private static IField ImportField(IField field, ModuleDefMD targetModule)
        {
            if (field.DeclaringType != null && field.DeclaringType.DefinitionAssembly == targetModule.Assembly)
            {
                var resolved = targetModule.Find(field.DeclaringType.FullName, false);
                if (resolved != null)
                {
                    var hits = resolved.Fields.Where(x => x.Name == field.Name && x.FieldSig.ToString() == field.FieldSig.ToString()).ToList();
                    if (hits.Count == 1) return hits[0];
                }
            }
            return targetModule.Import(field);
        }

        private static bool IsNamedMethod(object operand, string name)
        {
            var m = operand as IMethod;
            return m != null && m.Name == name;
        }

        private static int IndexOfFrom(IList<Instruction> ins, int start, Func<Instruction, bool> predicate)
        {
            for (int i = Math.Max(0, start); i < ins.Count; i++) if (predicate(ins[i])) return i;
            return -1;
        }

        private static int IndexOf(IList<Instruction> ins, Func<Instruction, bool> predicate)
        {
            for (int i = 0; i < ins.Count; i++) if (predicate(ins[i])) return i;
            return -1;
        }

        private static MethodDef FindSingleMethod(ModuleDefMD mod, string typeName, string methodName)
        {
            var t = mod.Find(typeName, false);
            if (t == null) throw new InvalidOperationException("type not found: " + typeName);
            var hits = t.Methods.Where(x => x.Name == methodName && x.HasBody).ToList();
            if (hits.Count != 1) throw new InvalidOperationException("expected one " + methodName + ", got " + hits.Count);
            return hits[0];
        }

        private static PatchResult Start(string id, string target)
        {
            return new PatchResult { Id = id, Target = target, Status = "failed" };
        }

        private static void Done(PatchResult r, string strategy, string detail, List<PatchResult> results)
        {
            r.Status = "applied";
            r.Strategy = strategy;
            r.Detail = detail;
            results.Add(r);
        }

        private static void Fail(PatchResult r, Exception ex, List<PatchResult> results)
        {
            r.Detail = ex.GetType().Name + ": " + ex.Message;
            r.Warnings.Add(ex.Message);
            results.Add(r);
        }

        private static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var fs = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToUpperInvariant();
        }
    }

    internal static class SimpleJson
    {
        public static string Serialize(object value)
        {
            var sb = new StringBuilder();
            Write(sb, value);
            return sb.ToString();
        }

        private static void Write(StringBuilder sb, object value)
        {
            if (value == null) { sb.Append("null"); return; }
            var s = value as string;
            if (s != null) { WriteString(sb, s); return; }
            if (value is bool) { sb.Append(((bool)value) ? "true" : "false"); return; }
            if (value is int || value is long || value is double) { sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); return; }
            var dict = value as System.Collections.IDictionary;
            if (dict != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(e.Key));
                    sb.Append(':');
                    Write(sb, e.Value);
                }
                sb.Append('}');
                return;
            }
            var seq = value as System.Collections.IEnumerable;
            if (seq != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    Write(sb, item);
                }
                sb.Append(']');
                return;
            }
            var props = value.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            sb.Append('{');
            bool firstProp = true;
            foreach (var p in props)
            {
                if (!firstProp) sb.Append(',');
                firstProp = false;
                WriteString(sb, p.Name);
                sb.Append(':');
                Write(sb, p.GetValue(value, null));
            }
            sb.Append('}');
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}