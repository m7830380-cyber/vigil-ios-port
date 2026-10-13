// Patches the retail VIGIL managed assemblies so they can be AOT-compiled with IL2CPP and run on iOS.
//
//   VigilPatcher patch    <retailManagedDir> <VigilShim.dll> <outDir>
//   VigilPatcher analyze  <managedDir>            (APIs the port cares about, per assembly)
//   VigilPatcher pinvokes <managedDir>            (every P/Invoke, by native library)
//   VigilPatcher verify   <patchedDir>            (fails if anything desktop-only is left)
//   + dumpil / types / memberrefs / calls / stackcheck / stubmethods (investigation helpers)
//
// All shipped assemblies:
//  * P/Invokes into FMOD (fmodstudio) and Steam Audio (phonon, phonon_fmod, audioplugin_phonon) are
//    retargeted to "__Internal": the real iOS static libraries are linked into the player.
//    Steam Audio's OpenCL / Radeon Rays / TrueAudioNext / Embree entry points become stubs that throw
//    DllNotFoundException (Steam Audio catches that and falls back to its CPU paths).
//  * every other P/Invoke (Steamworks, ...) becomes a stub that throws DllNotFoundException.
//  * Process.Start & co. go to VigilShim.Platform (logs and does nothing).
//  * Cursor.lockState / visible go to VigilShim.VCursor (iOS has no cursor; the touch layer reads it).
//  * Application.dataPath in the game's own code goes to VigilShim.Paths.DataPath (Documents/VigilData).
// Input-reading assemblies (game, UnityEngine.UI, TextMeshPro, GPUInstancer, SRP core, VFX):
//  * every UnityEngine.Input member goes to VigilShim.VInput (virtual mouse/keys/axes driven by touch).
// Assembly-CSharp:
//  * PipeReceiver no longer launches PBP.exe / taskkill: it starts and stops VigilShim.FaceBlink (ARKit).
//  * BlinkingManager's MouseToBlink mode reads the on-screen BLINK button (FaceBlink.GetManualBlink).
//  * the camera picker lists "Front camera (Face ID)" and never opens a WebCamTexture (ARKit owns the camera).
//  * SteamManager never initialises Steam (Initialized stays false, so achievements are skipped).
//  * Application.Quit, Screen.SetResolution / fullScreen and PlayerPrefs go to the shim
//    (PlayerPrefs -> Documents/Vigil-settings.txt).
// FMODUnity:
//  * plugins are not loaded from DLLs; the statically linked Steam Audio FMOD plugin is registered instead.
// Unity.Burst:
//  * Burst is reported disabled, so [BurstCompile] jobs and function pointers run as managed (IL2CPP) code.
using Mono.Cecil;
using Mono.Cecil.Cil;

static partial class Program
{
    const string ShimName = "VigilShim";

    // --pc-test: keep FMOD / Steam Audio pointing at their Windows DLLs so the patched assemblies can be
    // tried in a copy of the Windows game (everything else is patched exactly as for iOS).
    static bool PcTest;

    // Native libraries whose iOS static builds are linked into the player.
    static readonly HashSet<string> InternalLibs = new HashSet<string> { "fmodstudio", "phonon", "phonon_fmod", "audioplugin_phonon" };

    // Steam Audio entry points for features that do not exist on iOS (GPU / OpenCL / Embree).
    static bool IsUnsupportedSteamAudioEntry(string entry) =>
        entry.Contains("OpenCL") || entry.Contains("RadeonRays") || entry.Contains("TrueAudioNext") || entry.Contains("Embree");

    // Assemblies whose UnityEngine.Input reads are redirected to the virtual input.
    static readonly HashSet<string> InputRedirect = new HashSet<string>
    {
        "Assembly-CSharp", "Assembly-CSharp-firstpass", "UnityEngine.UI", "Unity.TextMeshPro", "GPUInstancer",
        "Unity.RenderPipelines.Core.Runtime", "Unity.VisualEffectGraph.Runtime",
    };

    static int Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "analyze") return Analyze.Run(args[1]);
            if (args.Length >= 2 && args[0] == "pinvokes") return Analyze.PInvokes(args[1]);
            if (args.Length >= 2 && args[0] == "verify") return Verify(args[1]);
            if (args.Length >= 2 && args[0] == "stackcheck") return Analyze.StackCheck(args.Skip(1).ToArray());
            if (args.Length >= 3 && args[0] == "dumpil") return Analyze.DumpIL(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "types") return Analyze.Types(args[1], args[2]);
            if (args.Length >= 4 && args[0] == "stubmethods") return Analyze.StubMethods(args[1], args[2], args[3]);
            if (args.Length >= 3 && args[0] == "memberrefs") return Analyze.MemberRefs(args[1], args[2]);
            if (args.Length >= 3 && args[0] == "calls") return Analyze.Calls(args[1], args[2]);
            if (args.Length >= 4 && args[0] == "patch") { PcTest = args.Contains("--pc-test"); return Patch(args[1], args[2], args[3]); }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("FATAL: " + e);
            return 3;
        }
        Console.Error.WriteLine("usage: VigilPatcher patch <managedDir> <VigilShim.dll> <outDir> | analyze|pinvokes|verify <dir>");
        return 1;
    }

    /// The game's own (non-engine, non-BCL) assemblies, from ScriptingAssemblies.json order: every dll in
    /// Managed that is not a UnityEngine module, a BCL facade or mscorlib.
    static List<string> ShippedAssemblies(string dir)
    {
        var list = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p, StringComparer.Ordinal))
        {
            string name = Path.GetFileName(path);
            if (name == "UnityEngine.dll" || (name.StartsWith("UnityEngine.") && name != "UnityEngine.UI.dll")) continue;
            if (name.StartsWith("System.") || name.StartsWith("Mono.") || name.StartsWith("Microsoft.") ||
                name == "mscorlib.dll" || name == "netstandard.dll" || name == "System.dll") continue;
            list.Add(name);
        }
        return list;
    }

    static int Patch(string inDir, string shimPath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(inDir);
        resolver.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(shimPath)));
        var rp = new ReaderParameters { AssemblyResolver = resolver, ReadingMode = ReadingMode.Immediate, ReadSymbols = false };
        var shim = AssemblyDefinition.ReadAssembly(shimPath, rp);

        int total = 0, failures = 0;
        var shipped = ShippedAssemblies(inDir);
        foreach (var name in shipped)
        {
            var asm = AssemblyDefinition.ReadAssembly(Path.Combine(inDir, name), rp);
            var ctx = new Ctx(asm.MainModule, shim.MainModule);
            var counts = new List<string>();
            int Step(string what, int n) { if (n > 0) counts.Add($"{what} {n}"); return n; }

            int n = 0;
            string an = asm.Name.Name;
            // game-specific rewrites first: they replace calls the generic passes would otherwise redirect
            if (an == "Assembly-CSharp") n += Step("game", PatchGame(ctx));
            if (an == "FMODUnity" && !PcTest) n += Step("fmod", PatchFmod(ctx));
            if (an == "Unity.Burst") n += Step("burst", PatchBurst(ctx));
            if (an == "SteamAudioUnity") n += Step("steamaudio", PatchSteamAudio(ctx));
            n += Step("pinvoke", RetargetPInvokes(ctx));
            n += Step("process", RedirectProcess(ctx));
            n += Step("cursor", RedirectCursor(ctx));
            if (an == "Assembly-CSharp" || an == "Assembly-CSharp-firstpass")
                n += Step("dataPath", RedirectStatic(ctx, "UnityEngine.Application", "get_dataPath", "Paths", "get_DataPath"));
            n += Step("typedref", RemoveTypedReferences(ctx));
            if (InputRedirect.Contains(an)) n += Step("input", RedirectInput(ctx));

            total += n;
            failures += ctx.Failures;
            asm.Write(Path.Combine(outDir, name));
            Console.WriteLine($"{name}: {n} patches{(counts.Count > 0 ? " (" + string.Join(", ", counts) + ")" : "")}{(ctx.Failures > 0 ? $", {ctx.Failures} FAILED" : "")}");
        }
        File.Copy(shimPath, Path.Combine(outDir, ShimName + ".dll"), true);
        Console.WriteLine($"{shipped.Count} assemblies, {total} patches, {failures} failures");
        if (failures > 0) return 2;
        return Verify(outDir);
    }

    // ------------------------------------------------------------------------------------------
    sealed class Ctx
    {
        public readonly ModuleDefinition Mod;
        public readonly ModuleDefinition Shim;
        public int Failures;
        public Ctx(ModuleDefinition mod, ModuleDefinition shim) { Mod = mod; Shim = shim; }

        public TypeDefinition ShimType(string name) => Shim.GetType(ShimName + "." + name) ?? throw new Exception("shim type missing: " + name);

        public MethodReference ShimMethod(string type, string name, params string[] paramTypes)
        {
            var m = ShimType(type).Methods.FirstOrDefault(x => x.Name == name && x.Parameters.Count == paramTypes.Length &&
                x.Parameters.Select(p => p.ParameterType.FullName).SequenceEqual(paramTypes));
            if (m == null) throw new Exception($"shim method missing: {type}.{name}({string.Join(",", paramTypes)})");
            return Mod.ImportReference(m);
        }

        public void Fail(string what)
        {
            Console.WriteLine("  !! " + what);
            Failures++;
        }
    }

    static IEnumerable<(MethodDefinition m, Instruction ins)> Calls(ModuleDefinition mod, Func<MethodDefinition, bool> where = null)
    {
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || (where != null && !where(m))) continue;
                foreach (var ins in m.Body.Instructions.ToList())
                    if ((ins.OpCode == OpCodes.Call || ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Newobj) && ins.Operand is MethodReference)
                        yield return (m, ins);
            }
    }

    static string Sig(MethodReference mr) => string.Join(",", mr.Parameters.Select(p => p.ParameterType.FullName));

    static MethodReference DnfCtor(ModuleDefinition mod)
    {
        var dnfType = new TypeReference("System", "DllNotFoundException", mod, mod.TypeSystem.CoreLibrary);
        var ctor = new MethodReference(".ctor", mod.TypeSystem.Void, dnfType) { HasThis = true };
        ctor.Parameters.Add(new ParameterDefinition(mod.TypeSystem.String));
        return ctor;
    }

    // ------------------------------------------------------------------------------------------
    static int RetargetPInvokes(Ctx c)
    {
        int n = 0;
        var mod = c.Mod;
        ModuleReference internalRef = null;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsPInvokeImpl || m.PInvokeInfo == null) continue;
                string lib = m.PInvokeInfo.Module.Name;
                string libKey = Path.GetFileNameWithoutExtension(lib).ToLowerInvariant();
                if (libKey.StartsWith("lib")) libKey = libKey.Substring(3);
                string entry = m.PInvokeInfo.EntryPoint ?? m.Name;
                if (lib == "__Internal") continue;
                if (PcTest && InternalLibs.Contains(libKey)) continue;
                if (InternalLibs.Contains(libKey) && !(libKey == "phonon" && IsUnsupportedSteamAudioEntry(entry)))
                {
                    if (internalRef == null)
                    {
                        internalRef = mod.ModuleReferences.FirstOrDefault(r => r.Name == "__Internal");
                        if (internalRef == null) { internalRef = new ModuleReference("__Internal"); mod.ModuleReferences.Add(internalRef); }
                    }
                    m.PInvokeInfo.Module = internalRef;
                    n++;
                    continue;
                }
                StubPInvoke(m, $"{lib} ({entry}) is not available on iOS");
                n++;
            }
        // drop now-unused module references (steam_api64, ...)
        var used = new HashSet<ModuleReference>(mod.GetTypes().SelectMany(t => t.Methods).Where(m => m.IsPInvokeImpl && m.PInvokeInfo != null).Select(m => m.PInvokeInfo.Module));
        foreach (var r in mod.ModuleReferences.ToList())
            if (!used.Contains(r)) mod.ModuleReferences.Remove(r);
        return n;
    }

    static void StubPInvoke(MethodDefinition m, string message)
    {
        m.PInvokeInfo = null;     // Cecil's setter turns IsPInvokeImpl back on,
        m.IsPInvokeImpl = false;  // so clear the flag afterwards
        m.IsPreserveSig = false;
        m.ImplAttributes &= ~MethodImplAttributes.PreserveSig;
        m.ImplAttributes |= MethodImplAttributes.IL | MethodImplAttributes.Managed;
        m.Body = new MethodBody(m);
        var il = m.Body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, message);
        il.Emit(OpCodes.Newobj, DnfCtor(m.Module));
        il.Emit(OpCodes.Throw);
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectProcess(Ctx c)
    {
        int n = 0;
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "System.Diagnostics.Process" || ins.OpCode == OpCodes.Newobj) continue;
            MethodReference to = null;
            if (mr.Name == "Start" && !mr.HasThis)
            {
                string sig = Sig(mr);
                if (sig == "System.Diagnostics.ProcessStartInfo") to = c.ShimMethod("Platform", "ProcessStart", "System.Diagnostics.ProcessStartInfo");
                else if (sig == "System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String");
                else if (sig == "System.String,System.String") to = c.ShimMethod("Platform", "ProcessStart", "System.String", "System.String");
                else { c.Fail($"{m.FullName}: unhandled Process.Start({sig})"); continue; }
            }
            else if (mr.Name == "Start" && mr.HasThis && mr.Parameters.Count == 0)
                to = c.ShimMethod("Platform", "ProcessStartInstance", "System.Diagnostics.Process");
            else if (mr.Name == "GetProcessesByName" && Sig(mr) == "System.String")
                to = c.ShimMethod("Platform", "GetProcessesByName", "System.String");
            if (to == null) continue;
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectCursor(Ctx c)
    {
        int n = 0;
        var vc = c.ShimType("VCursor");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Cursor") continue;
            if (mr.Name != "get_lockState" && mr.Name != "set_lockState" && mr.Name != "get_visible" && mr.Name != "set_visible") continue;
            var target = vc.Methods.First(x => x.Name == mr.Name);
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    /// Redirects every call to a static engine member to a shim method with the same signature.
    static int RedirectStatic(Ctx c, string type, string name, string shimType, string shimName, Func<MethodDefinition, bool> where = null)
    {
        int n = 0;
        MethodReference to = null;
        foreach (var (m, ins) in Calls(c.Mod, where))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != type || mr.Name != name || mr.HasThis) continue;
            if (to == null)
            {
                var def = c.ShimType(shimType).Methods.FirstOrDefault(x => x.Name == shimName && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
                if (def == null) { c.Fail($"no shim {shimType}.{shimName} for {type}.{name}({Sig(mr)})"); return n; }
                to = c.Mod.ImportReference(def);
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    static int RedirectInput(Ctx c)
    {
        int n = 0;
        var vinput = c.ShimType("VInput");
        foreach (var (m, ins) in Calls(c.Mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.Input") continue;
            var target = vinput.Methods.FirstOrDefault(x => x.Name == mr.Name && x.IsStatic &&
                x.Parameters.Count == mr.Parameters.Count && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (target == null)
            {
                c.Fail($"{m.FullName}: no VInput equivalent for Input.{mr.Name}({Sig(mr)})");
                continue;
            }
            ins.OpCode = OpCodes.Call;
            ins.Operand = c.Mod.ImportReference(target);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // IL2CPP does not model mkrefany (C# __makeref) well; none is expected in VIGIL - report any.
    static int RemoveTypedReferences(Ctx c)
    {
        foreach (var t in c.Mod.GetTypes())
            foreach (var m in t.Methods)
                if (m.HasBody && m.Body.Instructions.Any(i => i.OpCode.Code == Code.Mkrefany || i.OpCode.Code == Code.Refanyval ||
                                                              i.OpCode.Code == Code.Refanytype || i.OpCode.Code == Code.Arglist))
                    Console.WriteLine("  note: typed reference / arglist in " + m.FullName + " (IL2CPP may reject it)");
        return 0;
    }

    // ------------------------------------------------------------------------------------------
    static MethodDefinition Method(Ctx c, TypeDefinition t, string name, int paramCount = -1)
    {
        var m = t?.Methods.FirstOrDefault(x => x.Name == name && (paramCount < 0 || x.Parameters.Count == paramCount));
        if (m == null) c.Fail($"{t?.FullName ?? "<missing type>"}.{name} missing");
        return m;
    }

    static ILProcessor NewBody(MethodDefinition m)
    {
        m.Body = new MethodBody(m);
        return m.Body.GetILProcessor();
    }

    static int MakeNoOp(Ctx c, TypeDefinition t, string name)
    {
        var m = Method(c, t, name);
        if (m == null) return 0;
        if (m.ReturnType.FullName != "System.Void") { c.Fail($"{t.Name}.{name} is not void"); return 0; }
        NewBody(m).Emit(OpCodes.Ret);
        return 1;
    }

    static int ReturnConst(Ctx c, TypeDefinition t, string name, int value)
    {
        var m = Method(c, t, name);
        if (m == null) return 0;
        var il = NewBody(m);
        il.Emit(OpCodes.Ldc_I4, value);
        il.Emit(OpCodes.Ret);
        return 1;
    }

    static FieldDefinition Field(Ctx c, TypeDefinition t, string name)
    {
        var f = t?.Fields.FirstOrDefault(x => x.Name == name);
        if (f == null) c.Fail($"{t?.FullName}.{name} field missing");
        return f;
    }

    static MethodReference EngineMethod(ModuleDefinition mod, string asmName, string ns, string type, string name, TypeReference ret, bool hasThis, params TypeReference[] ps)
    {
        var scope = mod.AssemblyReferences.FirstOrDefault(r => r.Name == asmName);
        if (scope == null) { scope = new AssemblyNameReference(asmName, new Version(0, 0, 0, 0)); mod.AssemblyReferences.Add(scope); }
        var tr = new TypeReference(ns, type, mod, scope);
        var mr = new MethodReference(name, ret, tr) { HasThis = hasThis };
        foreach (var p in ps) mr.Parameters.Add(new ParameterDefinition(p));
        return mr;
    }

    // ------------------------------------------------------------------------------------------
    static int PatchGame(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;
        var log = EngineMethod(mod, "UnityEngine.CoreModule", "UnityEngine", "Debug", "Log", mod.TypeSystem.Void, false, mod.TypeSystem.Object);

        // --- blink: PipeReceiver -> ARKit (VigilShim.FaceBlink) ---------------------------------
        var pipe = mod.GetType("PipeReceiver");
        if (pipe == null) c.Fail("PipeReceiver missing");
        else
        {
            var blinkValue = Field(c, pipe, "blinkValue");
            var cameraID = Field(c, pipe, "cameraID");
            var isRunning = Field(c, pipe, "isRunning");
            var start = Method(c, pipe, "StartProgram", 0);
            var end = Method(c, pipe, "EndProgram", 0);
            if (blinkValue != null && cameraID != null && isRunning != null && start != null && end != null)
            {
                // StartProgram(): blinkValue = -2; isRunning = true; FaceBlink.Start(cameraID);
                var il = NewBody(start);
                il.Emit(OpCodes.Ldc_R4, -2f); il.Emit(OpCodes.Stsfld, blinkValue);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Stfld, isRunning);
                il.Emit(OpCodes.Ldsfld, cameraID);
                il.Emit(OpCodes.Call, c.ShimMethod("FaceBlink", "Start", "System.Single"));
                il.Emit(OpCodes.Ret);
                // EndProgram(): isRunning = false; FaceBlink.Stop(); blinkValue = -2;
                il = NewBody(end);
                il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, isRunning);
                il.Emit(OpCodes.Call, c.ShimMethod("FaceBlink", "Stop"));
                il.Emit(OpCodes.Ldc_R4, -2f); il.Emit(OpCodes.Stsfld, blinkValue);
                il.Emit(OpCodes.Ret);
                n += 2;
            }
            // the PBP.exe launcher and its stdout reader thread are gone entirely
            n += MakeNoOp(c, pipe, "StartPythonProcess");
            n += MakeNoOp(c, pipe, "ReceiveData");
        }

        // --- BlinkingManager.MouseToBlink: right mouse button -> on-screen BLINK button --------------
        var bm = mod.GetType("BlinkingManager");
        var bmUpdate = Method(c, bm, "Update", 0);
        if (bmUpdate != null)
        {
            int k = 0;
            foreach (var ins in bmUpdate.Body.Instructions)
                if (ins.OpCode == OpCodes.Call && ins.Operand is MethodReference mr && mr.DeclaringType.FullName == "UnityEngine.Input" && mr.Name == "GetMouseButton")
                {
                    ins.Operand = c.ShimMethod("FaceBlink", "GetManualBlink", "System.Int32");
                    k++;
                }
            if (k != 1) c.Fail($"BlinkingManager.Update: expected 1 GetMouseButton call, found {k}");
            n += k;
        }

        // --- camera picker: one fake "Front camera (Face ID)" device, no WebCamTexture -------------------
        bool InPicker(MethodDefinition m) => m.DeclaringType.Name == "CameraSelectUI" || m.DeclaringType.Name == "WebcamDisplay" ||
                                            m.DeclaringType.DeclaringType?.Name == "CameraSelectUI" || m.DeclaringType.DeclaringType?.Name == "WebcamDisplay";
        int cam = 0;
        foreach (var (m, ins) in Calls(mod, InPicker))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.WebCamTexture") continue;
            MethodReference to = null;
            if (mr.Name == "get_devices") to = c.ShimMethod("FaceBlink", "Devices");
            else if (mr.Name == ".ctor" && Sig(mr) == "System.String") to = c.ShimMethod("FaceBlink", "NewPreview", "System.String");
            else if (mr.Name == "Play" && mr.Parameters.Count == 0) to = c.ShimMethod("FaceBlink", "PlayPreview", "UnityEngine.WebCamTexture");
            else if (mr.Name == "Stop" && mr.Parameters.Count == 0) to = c.ShimMethod("FaceBlink", "StopPreview", "UnityEngine.WebCamTexture");
            else { c.Fail($"{m.FullName}: unhandled WebCamTexture.{mr.Name}"); continue; }
            ins.OpCode = OpCodes.Call;
            ins.Operand = to;
            cam++;
        }
        if (cam < 4) c.Fail($"camera picker: only {cam} WebCamTexture uses rewritten");
        n += cam;
        foreach (var (m, ins) in Calls(mod, x => !InPicker(x)))
            if (((MethodReference)ins.Operand).DeclaringType.FullName == "UnityEngine.WebCamTexture")
                c.Fail($"{m.FullName}: unexpected WebCamTexture use");

        // --- Steam: never initialised --------------------------------------------------------------
        var steam = mod.GetType("SteamManager");
        var steamAwake = Method(c, steam, "Awake", 0);
        if (steamAwake != null)
        {
            var sInstance = Field(c, steam, "s_instance");
            var core = mod.AssemblyReferences.First(r => r.Name == "UnityEngine.CoreModule");
            var objType = new TypeReference("UnityEngine", "Object", mod, core);
            var ddol = new MethodReference("DontDestroyOnLoad", mod.TypeSystem.Void, objType);
            ddol.Parameters.Add(new ParameterDefinition(objType));
            var compType = new TypeReference("UnityEngine", "Component", mod, core);
            var getGo = new MethodReference("get_gameObject", new TypeReference("UnityEngine", "GameObject", mod, core), compType) { HasThis = true };
            var destroy = new MethodReference("Destroy", mod.TypeSystem.Void, objType);
            destroy.Parameters.Add(new ParameterDefinition(objType));
            var opIneq = new MethodReference("op_Inequality", mod.TypeSystem.Boolean, objType);
            opIneq.Parameters.Add(new ParameterDefinition(objType));
            opIneq.Parameters.Add(new ParameterDefinition(objType));

            // if (s_instance != null) { Destroy(gameObject); return; }
            // s_instance = this; DontDestroyOnLoad(gameObject); Debug.Log("..."); (m_bInitialized stays false)
            var il = NewBody(steamAwake);
            var cont = il.Create(OpCodes.Nop);
            il.Emit(OpCodes.Ldsfld, sInstance); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Call, opIneq);
            il.Emit(OpCodes.Brfalse, cont);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGo); il.Emit(OpCodes.Call, destroy);
            il.Emit(OpCodes.Ret);
            il.Append(cont);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stsfld, sInstance);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGo); il.Emit(OpCodes.Call, ddol);
            il.Emit(OpCodes.Ldstr, "[Vigil-iOS] Steamworks disabled on iOS (achievements are skipped)");
            il.Emit(OpCodes.Call, log);
            il.Emit(OpCodes.Ret);
            n++;
        }

        // --- desktop-only engine calls ----------------------------------------------------------------
        n += RedirectStatic(c, "UnityEngine.Application", "Quit", "Platform", "Quit");
        n += RedirectStatic(c, "UnityEngine.Screen", "SetResolution", "Platform", "SetResolution");
        n += RedirectStatic(c, "UnityEngine.Screen", "set_fullScreen", "Platform", "SetFullScreen");
        n += RedirectStatic(c, "UnityEngine.Screen", "get_fullScreen", "Platform", "GetFullScreen");

        // --- PlayerPrefs -> Documents/Vigil-settings.txt ----------------------------------------------
        var prefs = c.ShimType("Prefs");
        foreach (var (m, ins) in Calls(mod))
        {
            var mr = (MethodReference)ins.Operand;
            if (mr.DeclaringType.FullName != "UnityEngine.PlayerPrefs") continue;
            var t = prefs.Methods.FirstOrDefault(x => x.Name == mr.Name && Sig(x) == Sig(mr) && x.ReturnType.FullName == mr.ReturnType.FullName);
            if (t == null) { c.Fail($"{m.FullName}: no Prefs.{mr.Name}({Sig(mr)})"); continue; }
            ins.OpCode = OpCodes.Call;
            ins.Operand = mod.ImportReference(t);
            n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // FMODUnity.Platform.LoadDynamicPlugins: no DLLs on iOS -> nothing.
    // FMODUnity.Platform.LoadStaticPlugins(FMOD.System coreSystem, Action<RESULT,string> report):
    //     VigilShim.FmodPlugins.RegisterStaticPlugins(coreSystem.handle);
    static int PatchFmod(Ctx c)
    {
        var mod = c.Mod;
        int n = 0;
        var platform = mod.GetType("FMODUnity.Platform");
        n += MakeNoOp(c, platform, "LoadDynamicPlugins");
        var ls = Method(c, platform, "LoadStaticPlugins", 2);
        var sysType = mod.GetType("FMOD.System");
        var handle = Field(c, sysType, "handle");
        if (ls != null && handle != null)
        {
            if (ls.Parameters[0].ParameterType.FullName != "FMOD.System") c.Fail("LoadStaticPlugins: unexpected signature " + ls.FullName);
            var il = NewBody(ls);
            il.Emit(OpCodes.Ldarga_S, ls.Parameters[0]);
            il.Emit(OpCodes.Ldfld, handle);
            il.Emit(OpCodes.Call, c.ShimMethod("FmodPlugins", "RegisterStaticPlugins", "System.IntPtr"));
            il.Emit(OpCodes.Ret);
            n++;
        }
        // overrides that bypass the base implementation must not exist (PlatformPlayInEditor is editor-only)
        foreach (var t in mod.GetTypes())
            if (t != platform && t.Name != "PlatformPlayInEditor" && t.Methods.Any(m => m.Name == "LoadStaticPlugins" || m.Name == "LoadDynamicPlugins"))
                c.Fail("FMOD platform override of plugin loading in " + t.FullName);
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // No Burst-compiled native code exists for iOS (lib_burst_generated is a Windows DLL), so make Burst
    // report itself disabled: jobs and function pointers then run their managed (IL2CPP) code.
    static int PatchBurst(Ctx c)
    {
        int n = 0;
        var compiler = c.Mod.GetType("Unity.Burst.BurstCompiler");
        var isEnabled = compiler?.Methods.FirstOrDefault(m => m.Name == "get_IsEnabled" && m.IsStatic);
        if (isEnabled == null) c.Fail("BurstCompiler.IsEnabled missing");
        else { var il = NewBody(isEnabled); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); n++; }
        var opts = c.Mod.GetType("Unity.Burst.BurstCompilerOptions");
        foreach (var name in new[] { "get_EnableBurstCompilation", "get_IsEnabled" })
        {
            var g = opts?.Methods.FirstOrDefault(m => m.Name == name && !m.IsStatic);
            if (g == null) continue;
            var il = NewBody(g); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ret); n++;
        }
        return n;
    }

    // ------------------------------------------------------------------------------------------
    // A bug of the retail game (also in its Windows logs): a probe batch whose OnEnable ran before the Steam Audio
    // simulator existed keeps a null native wrapper, and every SteamAudioSource using it for pathing then throws
    // NullReferenceException in SetInputs every frame. Exceptions are expensive under IL2CPP, so
    // SteamAudioProbeBatch.GetProbeBatch() returns IntPtr.Zero (= no probe batch) instead of throwing.
    static int PatchSteamAudio(Ctx c)
    {
        var t = c.Mod.GetType("SteamAudio.SteamAudioProbeBatch");
        var m = Method(c, t, "GetProbeBatch", 0);
        if (m == null) return 0;
        var field = Field(c, t, "mProbeBatch");
        var get = c.Mod.GetType("SteamAudio.ProbeBatch")?.Methods.FirstOrDefault(x => x.Name == "Get" && x.Parameters.Count == 0);
        if (field == null || get == null) { c.Fail("SteamAudio.ProbeBatch.Get missing"); return 0; }
        var zero = new FieldReference("Zero", m.ReturnType, m.ReturnType);
        var il = NewBody(m);
        var notNull = il.Create(OpCodes.Callvirt, get);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldfld, field);
        il.Emit(OpCodes.Dup);
        il.Emit(OpCodes.Brtrue_S, notNull);
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ldsfld, zero);
        il.Emit(OpCodes.Ret);
        il.Append(notNull);
        il.Emit(OpCodes.Ret);

        // ...but a source must then not ask the native simulator for pathing with no probe batch: that crashes
        // phonon (iplSimulatorRunPathing on the simulation thread). In SteamAudioSource.SetInputs, right after
        // "inputs.pathingProbes = ...", a zero handle clears pathingProbeBatch, so the game's own check below it
        // turns pathing off for that source ("Pathing probe batch not set, disabling pathing for source X").
        var src = c.Mod.GetType("SteamAudio.SteamAudioSource");
        var setInputs = Method(c, src, "SetInputs", 1);
        var batchField = Field(c, src, "pathingProbeBatch");
        if (setInputs == null || batchField == null) return 1;
        var body = setInputs.Body;
        var store = body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Stfld && i.Operand is FieldReference f && f.Name == "pathingProbes");
        var inputsVar = body.Variables.FirstOrDefault(v => v.VariableType.Name == "SimulationInputs");
        if (store == null || inputsVar == null) { c.Fail("SteamAudioSource.SetInputs: pathingProbes store not found"); return 1; }
        var probesField = (FieldReference)store.Operand;
        var proc = body.GetILProcessor();
        var after = store.Next;
        var opEq = new MethodReference("op_Equality", c.Mod.TypeSystem.Boolean, c.Mod.TypeSystem.IntPtr) { HasThis = false };
        opEq.Parameters.Add(new ParameterDefinition(c.Mod.TypeSystem.IntPtr));
        opEq.Parameters.Add(new ParameterDefinition(c.Mod.TypeSystem.IntPtr));
        foreach (var ins in new[]
        {
            proc.Create(OpCodes.Ldloca, inputsVar),
            proc.Create(OpCodes.Ldfld, probesField),
            proc.Create(OpCodes.Ldsfld, new FieldReference("Zero", c.Mod.TypeSystem.IntPtr, c.Mod.TypeSystem.IntPtr)),
            proc.Create(OpCodes.Call, opEq),
            proc.Create(OpCodes.Brfalse, after),
            proc.Create(OpCodes.Ldarg_0),
            proc.Create(OpCodes.Ldnull),
            proc.Create(OpCodes.Stfld, batchField),
        })
            proc.InsertBefore(after, ins);
        return 2;
    }

    // ------------------------------------------------------------------------------------------
    /// Fails if a patched assembly still references something that cannot work on iOS.
    static int Verify(string dir)
    {
        int problems = 0;
        void Problem(string s) { Console.WriteLine("  VERIFY: " + s); problems++; }
        foreach (var path in Directory.GetFiles(dir, "*.dll").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name == ShimName || name == "VigilIOS") continue;
            var mod = ModuleDefinition.ReadModule(path);
            foreach (var t in mod.GetTypes())
                foreach (var m in t.Methods)
                {
                    if (m.IsPInvokeImpl && m.PInvokeInfo != null && m.PInvokeInfo.Module.Name != "__Internal" && !(PcTest && InternalLibs.Contains(m.PInvokeInfo.Module.Name)))
                        Problem($"{name}: P/Invoke to {m.PInvokeInfo.Module.Name} in {m.FullName}");
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        if (!(ins.Operand is MethodReference mr)) continue;
                        string dt = mr.DeclaringType.FullName;
                        if (dt == "System.Diagnostics.Process" && (mr.Name == "Start" || mr.Name == "GetProcessesByName"))
                            Problem($"{name}: process launch left in {m.FullName}");
                        if (dt == "UnityEngine.Cursor" && (mr.Name == "set_lockState" || mr.Name == "set_visible"))
                            Problem($"{name}: Cursor.{mr.Name} left in {m.FullName}");
                        if (dt == "UnityEngine.Input" && InputRedirect.Contains(name))
                            Problem($"{name}: Input.{mr.Name} left in {m.FullName}");
                        if (dt == "UnityEngine.WebCamTexture" && name == "Assembly-CSharp")
                            Problem($"{name}: WebCamTexture.{mr.Name} left in {m.FullName}");
                    }
                }
        }
        Console.WriteLine(problems == 0 ? "verify: OK" : $"verify: {problems} problem(s)");
        return problems == 0 ? 0 : 4;
    }
}
