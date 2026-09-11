using System;
using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Utils;

public abstract class BaseCallingConventionResolver
{
    /// <summary>
    /// Cate sloturi de argument de pe stiva primeste un apel nerezolvat. 0 - adica niciunul, exact ce era
    /// pana acum - e valoarea implicita; CPP2IL_STACK_ARGS=4 le porneste.
    ///
    /// Un apel nerezolvat primeste azi doar registrele de argument, deci argumentele de pe stiva nu exista
    /// deloc in IR. Cand tinta se rezolva mai tarziu, <see cref="RemapRawArguments"/> se opreste tacit la
    /// capatul registrelor, iar la emisie fiecare argument lipsa devine un <c>PushDefaultOf</c> - deci un
    /// zero sau un null in locul valorii reale. Nu e un marker, e cod care compileaza si minte.
    ///
    /// Masurat pe cele 150 de DLL-uri recuperate, 131.705 de metode: 90,1% incap in cele patru registre
    /// intregi si n-au nevoie de nimic, iar patru sloturi de stiva acopera 99,2%. De aceea patru e numarul
    /// care merita incercat primul - sase duc la 99,8% si opt la 99,9%, dar fiecare slot in plus e inca un
    /// operand pe fiecare apel nerezolvat din proiect.
    ///
    /// Ramane pe 0 fiindca n-am putut masura. Schimbarea atinge lista de operanzi a fiecarui apel
    /// nerezolvat, deci si apelurile care nu se rezolva niciodata capata citiri din sloturi pe care poate
    /// nu le-a scris nimeni; asta tine in viata stocari pe care altfel le-ar sterge eliminarea de cod mort
    /// si poate cobori compilarea in loc s-o urce. E exact forma in care <c>CPP2IL_STACK_PARAMS</c> de
    /// alaturi s-a dovedit mai rea la masuratoare - dar aici masuratoarea a iesit invers: 13.354 -> 13.413
    /// cu patru sloturi, si rata celor fara marker care compileaza 89,8% -> 90,1%. Deci patru e implicitul,
    /// iar <c>CPP2IL_STACK_ARGS=0</c> revine la comportamentul de dinainte, in care argumentele de pe stiva
    /// erau inlocuite tacit cu zerouri de PushDefaultOf.
    ///
    /// Patru fiindca acopera 99,2% din metode; sase ar da 99,8% si opt 99,9%, dar fiecare slot in plus e o
    /// citire dintr-un loc pe care poate nu l-a scris nimeni.
    /// </summary>
    private static readonly int StackArgumentSlots =
        int.TryParse(Environment.GetEnvironmentVariable("CPP2IL_STACK_ARGS"), out var configured)
            ? Math.Max(0, Math.Min(configured, 16))
            : 4;

    /// <summary>
    /// Sloturile de argument de pe stiva pe care le vede apelantul, in ordinea argumentelor, sau nimic unde
    /// conventia nu e descrisa aici. Offset-urile sunt relative la rsp la momentul apelului, ca tot ce
    /// produce liftul pentru <c>[rsp+k]</c>, fiindca <c>StackAnalyzer.CorrectOffsets</c> adauga starea
    /// stivei la amandoua deopotriva - deci slotul de aici si stocarea care l-a scris ajung la acelasi nume.
    /// </summary>
    protected virtual StackOffset[] RawStackSlots(ApplicationAnalysisContext app) => [];

    // Numarul cerut prin mediu, ca sa nu-l citeasca fiecare subclasa separat.
    protected static int RequestedStackArgumentSlots => StackArgumentSlots;

    public static bool IsFloatingPoint(TypeAnalysisContext type)
        => type == type.AppContext.SystemTypes.SystemSingleType || type == type.AppContext.SystemTypes.SystemDoubleType;

    protected static bool IsFloatingPoint(ParameterAnalysisContext par) => IsFloatingPoint(par.ParameterType);

    public abstract Register ReturnRegister(MethodAnalysisContext ctx);

    public abstract bool ReturnsViaHiddenBuffer(MethodAnalysisContext ctx);

    public abstract Register? HiddenReturnBufferRegister(MethodAnalysisContext ctx);

    public abstract IOperand[] ResolveForManaged(MethodAnalysisContext ctx);

    protected abstract (string[] Integer, string[] Float) RawRegisters(ApplicationAnalysisContext app);

    // MSVC style: argument slot n is integer register n or float register n, not both
    protected virtual bool UsesShadowedArgumentSlots(ApplicationAnalysisContext app) => false;

    // false when the return buffer pointer lives outside the argument registers (e.g. arm64 uses x8)
    protected virtual bool HiddenBufferConsumesArgumentSlot => true;

    /// <param name="isTailCall">
    /// Un salt-coada, nu un apel. Atunci nu se dau sloturi de stiva, si nu din prudenta: un apel obisnuit
    /// isi scrie argumentele de pe stiva in zona de iesire de la [rsp+0x20] inainte de a apela, dar un
    /// salt-coada nu scrie nimic - pleaca cu cele pe care le-a primit el, lasate unde erau. Dezasamblarea a
    /// 1.668 de astfel de thunk-uri din GameAssembly.dll arata asta direct: 1.487 citesc de pe stiva si
    /// exact zero scriu in zona de iesire. Un slot de la [rsp+0x20] pus pe un salt-coada ar numi deci alta
    /// valoare decat argumentul, adica fix minciuna pe care schimbarea asta o repara in alta parte.
    /// </param>
    public IOperand[] ResolveForUnmanaged(ApplicationAnalysisContext app, ulong target, bool isTailCall = false)
    {
        // We don't know the callee's signature, so preserve every argument register - and, where the
        // convention says where they sit, the first few outgoing stack slots as well, for the same reason:
        // the callee may be reading them, and nothing later can recover a value the IR never carried.

        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var registers = integerRegisters.Concat(floatRegisters).Select(name => (IOperand)new Register(null, name));

        return isTailCall
            ? registers.ToArray()
            : registers.Concat(RawStackSlots(app).Select(slot => (IOperand)slot)).ToArray();
    }

    public bool HasRawArgumentLayout(Instruction call, ApplicationAnalysisContext app)
        => PresentStackSlots(call, app) >= 0;

    /// <summary>
    /// Cate sloturi de stiva poarta chiar apelul asta, sau -1 daca operanzii lui nu sunt asezarea bruta
    /// deloc.
    ///
    /// Sunt doua forme valide, nu una. Un salt-coada n-a primit sloturi de la
    /// <see cref="ResolveForUnmanaged"/>, iar recuperarea dispecerizarii il face <c>Call</c> inainte de
    /// remapare, deci ajunge aici cu registre si atat; un apel obisnuit vine cu sloturile de dupa ele.
    /// Daca s-ar cere o singura forma, pornirea comutatorului ar opri remaparea cu totul pe toata familia
    /// de salturi-coada - adica ar strica fix ce vrea sa repare.
    /// </summary>
    private int PresentStackSlots(Instruction call, ApplicationAnalysisContext app)
    {
        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var argBase = ArgBase(call);
        var registerCount = integerRegisters.Length + floatRegisters.Length;
        var present = call.Operands.Count - argBase - registerCount;

        if (present != 0 && present != RawStackSlots(app).Length)
            return -1;

        for (var i = 0; i < integerRegisters.Length; i++)
            if (RegisterName(call.Operands[argBase + i]) != integerRegisters[i])
                return -1;

        for (var i = 0; i < floatRegisters.Length; i++)
            if (RegisterName(call.Operands[argBase + integerRegisters.Length + i]) != floatRegisters[i])
                return -1;

        // Numai ca sunt sloturi de stiva, nu si care anume. Offset-ul cu care au fost create era relativ la
        // rsp, dar StackAnalyzer a rulat intre timp: l-a facut relativ la intrarea in metoda si a inlocuit
        // operandul cu un registru fals numit dupa el, deci numarul de la creare nu mai exista nicaieri.
        // Pozitia ajunge - cele opt registre de deasupra sunt verificate exact, si nimic altceva nu pune
        // sloturi de stiva fix dupa ele.
        for (var i = 0; i < present; i++)
            if (RegisterName(call.Operands[argBase + registerCount + i]) is not { } name
                || !name.StartsWith(StackSlotPrefix, StringComparison.Ordinal))
                return -1;

        return present;
    }

    /// <summary>
    /// Muta operanzii bruti - registrele de argument asa cum le-a lasat liftul - pe sloturile pe care le
    /// cere chiar semnatura tintei, acum cunoscuta.
    ///
    /// Un slot care nu incape in registre se ia din sloturile de stiva, daca au fost cerute prin
    /// <c>CPP2IL_STACK_ARGS</c>. Fara ele bucla se opreste unde se oprea si inainte, iar argumentele de
    /// dincolo de registre raman nemapate - si atunci emisia le inlocuieste cu <c>PushDefaultOf</c>, adica
    /// un zero in loc de valoarea reala. De aceea numarul de sloturi conteaza: el decide de la al catelea
    /// argument incepe minciuna.
    /// </summary>
    public void RemapRawArguments(Instruction call, MethodAnalysisContext resolved)
    {
        var app = resolved.AppContext;

        var stackSlots = PresentStackSlots(call, app);
        if (stackSlots < 0)
            return;

        var (integerRegisters, floatRegisters) = RawRegisters(app);
        var stackBase = integerRegisters.Length + floatRegisters.Length;
        var argBase = ArgBase(call);

        var slots = new List<(bool IsFloat, bool Emit)>();
        if (ReturnsViaHiddenBuffer(resolved) && HiddenBufferConsumesArgumentSlot)
            slots.Add((false, false));
        if (!resolved.IsStatic)
            slots.Add((false, true));
        foreach (var parameter in resolved.Parameters)
            slots.Add((IsFloatingPoint(parameter), true));
        slots.Add((false, true)); // the MethodInfo argument

        var operands = new List<IOperand>(argBase + slots.Count);
        for (var i = 0; i < argBase; i++)
            operands.Add(call.Operands[i]);

        if (UsesShadowedArgumentSlots(app))
        {
            // Slotul n e registrul n, intreg sau flotant dupa tipul argumentului, si de la al patrulea
            // incolo e slotul de stiva n-4: sub msvc numerotarea e comuna, deci un argument nu schimba
            // pozitia celor de dupa el indiferent ce fel e.
            for (var slot = 0; slot < slots.Count; slot++)
            {
                int index;
                if (slot < integerRegisters.Length)
                    index = slots[slot].IsFloat ? integerRegisters.Length + slot : slot;
                else if (slot - integerRegisters.Length < stackSlots)
                    index = stackBase + (slot - integerRegisters.Length);
                else
                    break;

                if (slots[slot].Emit)
                    operands.Add(call.Operands[argBase + index]);
            }
        }
        else
        {
            // independent integer/float counters, and one shared counter for what spills: sub SysV un
            // argument ajunge pe stiva doar cand fisierul lui de registre s-a terminat, iar celalalt poate
            // avea loc in continuare, deci stiva se umple in ordinea argumentelor care chiar au dat pe
            // dinafara, nu in ordinea lor generala.
            var (integer, floating, spilled) = (0, 0, 0);

            foreach (var (isFloat, emit) in slots)
            {
                int index;
                if (isFloat ? floating < floatRegisters.Length : integer < integerRegisters.Length)
                    index = isFloat ? integerRegisters.Length + floating++ : integer++;
                else if (spilled < stackSlots)
                    index = stackBase + spilled++;
                else
                    break;

                if (emit)
                    operands.Add(call.Operands[argBase + index]);
            }
        }

        call.SetOperands(operands);
    }

    protected static int ArgBase(Instruction call) => call.OpCode is OpCode.CallVoid ? 1 : 2;

    // Numele pe care StackAnalyzer.NameForSlot il da unui slot dupa ce il transforma in registru fals.
    // Daca se schimba acolo, verificarea de mai sus nu mai recunoaste niciun slot si remaparea se opreste
    // la registre - adica exact comportamentul cu CPP2IL_STACK_ARGS=0, deci greseala e vizibila ca lipsa de
    // efect, nu ca argumente puse gresit.
    private const string StackSlotPrefix = "stack_";

    private static string? RegisterName(IOperand operand) => operand switch
    {
        Register register => register.Name,
        LocalVariable { Register.Name: var name } => name,
        _ => null
    };
}
