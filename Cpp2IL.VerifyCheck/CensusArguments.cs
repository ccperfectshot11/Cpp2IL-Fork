using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Cpp2IL.VerifyCore;

namespace Cpp2IL.VerifyCheck;

// Cum se fabrica un apel pentru o metoda pe care calea de COMPARATIE nu ar atinge-o niciodata.
//
// Selector.cs are dreptate sa refuze aici tot ce refuza: o comparatie are nevoie ca cele doua procese
// sa construiasca EXACT aceeasi valoare, si niciun proces nu poate promite asta pentru un obiect de pe
// heap. Recensamantul nu compara nimic, deci constrangerea dispare cu totul si ramane doar intrebarea
// "exista vreo valoare de tipul asta pe care sa i-o dam".
//
// Raspunsul degenerat - null pentru o clasa, "" pentru un string, un array gol, o structura pe zero,
// un obiect nechemat prin constructor - este un raspuns cinstit la intrebarea "ruleaza", si un raspuns
// slab la orice altceva. De aceea fiecare falsificare este NUMITA si tinuta in rezultat: "a rulat
// curat" peste argumente numai null spune mult mai putin decat pare, iar raportul trebuie sa poata
// spune diferenta in loc sa o ascunda intr-un procent.
internal enum ArgStrategy
{
    Faithful,        // primitiva sau structura numai din primitive: exact ce ar da si calea de comparatie
    NullReference,   // clasa, interfata, delegat: null
    EmptyString,     // string: sirul gol, nu null - un corp care indexeaza un string vede o lungime, nu o exceptie
    EmptyArray,      // array: lungime zero pe fiecare dimensiune
    ZeroedStruct,    // structura care atinge referinte: default(T), adica toate campurile pe zero
    ByRefBox,        // T&: o cutie cu strategia elementului, pe care reflectia o scrie inapoi
}

internal enum ReceiverStrategy
{
    None,            // metoda statica
    Faithful,        // structura numai din primitive
    ZeroedStruct,    // structura care atinge referinte
    Uninitialised,   // clasa: alocata FARA sa se cheme vreun constructor - vezi nota de la Plan
}

internal sealed class CensusPlan
{
    public bool Ok;
    public CensusOutcome Refusal;          // valabil doar cand Ok este fals
    public string RefusalDetail;

    public MethodBase Method;              // posibil o instantiere inchisa a celei primite
    public bool GenericGuess;              // metoda a fost inchisa cu argumente de tip ghicite
    public string GenericGuessDetail;

    public ReceiverStrategy Receiver;
    public Type ReceiverType;
    public ValueShape ReceiverShape;

    public ArgStrategy[] Strategies;
    public ArgStrategy[] ByRefInner;       // strategia din spatele unui T&, altfel Faithful nefolosit
    public Type[] ParameterTypes;          // tipul efectiv de construit (elementul, pentru T&)
    public ValueShape[] Shapes;            // nenul doar pe pozitiile Faithful

    // Citita si pe planurile refuzate, unde Strategies nu a apucat sa fie alocat: un refuz are si el o
    // calitate de raportat, si anume niciuna.
    public string Quality => Degenerate ? "degenerate"
        : Strategies == null || (Strategies.Length == 0 && Receiver == ReceiverStrategy.None) ? "none"
        : "faithful";
    public bool Degenerate;
    public List<string> Degeneracies = new();
}

internal static class CensusArguments
{
    // Argumentele de tip cu care se incearca inchiderea unei metode generice, in ordine. int intai:
    // este singurul care satisface o constrangere de tip valoare, si cele mai multe generice din cod
    // de joc sunt peste valori. Nu exista nicio pretentie ca instantierea aleasa este cea pe care o
    // foloseste jocul - de aceea rezultatul poarta GenericGuess si raportul le numara separat.
    private static readonly Type[] GenericGuesses = [typeof(int), typeof(object), typeof(string)];

    public static CensusPlan Plan(MethodBase method, bool allowGenerics, bool allowDegenerate)
    {
        var plan = new CensusPlan { Method = method };

        // Un .cctor nu se cheama: runtime-ul il ruleaza singur la prima atingere a tipului, iar a-l
        // chema a doua oara nu este acelasi lucru cu a-l chema o data. Nu e o pierdere - fiecare metoda
        // a tipului il declanseaza oricum, si asta se vede in categoria TypeInitFailed.
        if (method.IsConstructor && method.IsStatic)
            return Refuse(plan, CensusOutcome.NotAttemptedStaticCtor, "static constructor");

        if (method.DeclaringType == null)
            return Refuse(plan, CensusOutcome.NotAttemptedResolveFailed, "no declaring type");

        if (method.ContainsGenericParameters || method.DeclaringType.ContainsGenericParameters)
        {
            if (!allowGenerics)
                return Refuse(plan, CensusOutcome.NotAttemptedGeneric, "generic (guessing disabled)");

            if (!TryClose(method, plan, out var closed, out var why))
                return Refuse(plan, CensusOutcome.NotAttemptedGeneric, why);

            plan.Method = method = closed;
        }

        var declaring = method.DeclaringType;

        if (!method.IsStatic)
        {
            if (!PlanReceiver(plan, declaring, allowDegenerate, out var why))
                return Refuse(plan, CensusOutcome.NotAttemptedReceiver, why);
        }
        else
        {
            plan.Receiver = ReceiverStrategy.None;
        }

        var parameters = method.GetParameters();
        plan.Strategies = new ArgStrategy[parameters.Length];
        plan.ByRefInner = new ArgStrategy[parameters.Length];
        plan.ParameterTypes = new Type[parameters.Length];
        plan.Shapes = new ValueShape[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var type = parameters[i].ParameterType;
            if (type.IsByRef)
            {
                var element = type.GetElementType();
                if (!PlanValue(plan, element, allowDegenerate, out var innerStrategy, out var innerShape, out var innerWhy))
                    return Refuse(plan, CensusOutcome.NotAttemptedArguments, "parameter " + i + ": " + innerWhy);

                plan.Strategies[i] = ArgStrategy.ByRefBox;
                plan.ByRefInner[i] = innerStrategy;
                plan.ParameterTypes[i] = element;
                plan.Shapes[i] = innerShape;

                // Un parametru prin referinta nu este o falsificare a VALORII, dar este o abatere de la
                // ce ar accepta calea de comparatie, si tot ce o metoda scrie prin el se pierde.
                Degenerate(plan, "byref");
                continue;
            }

            if (!PlanValue(plan, type, allowDegenerate, out var strategy, out var shape, out var whyArg))
                return Refuse(plan, CensusOutcome.NotAttemptedArguments, "parameter " + i + ": " + whyArg);

            plan.Strategies[i] = strategy;
            plan.ParameterTypes[i] = type;
            plan.Shapes[i] = shape;
        }

        plan.Ok = true;
        return plan;
    }

    private static bool PlanReceiver(CensusPlan plan, Type declaring, bool allowDegenerate, out string why)
    {
        why = null;
        plan.ReceiverType = declaring;

        if (declaring.IsInterface || declaring.IsAbstract)
        {
            // S-ar putea cauta un subtip concret in acelasi assembly, dar asta ar masura corpul unei ALTE
            // metode ori de cate ori subtipul suprascrie, si ar cere o enumerare completa de tipuri peste
            // metadate recuperate - chiar drumul pe care ReflectionTypeLoadException asteapta. Numarate,
            // nu incercate; vezi nota lui EligibleClassReceiver din Selector.cs pentru acelasi tipar.
            why = declaring.IsInterface ? "declaring type is an interface" : "declaring type is abstract";
            return false;
        }

        if (declaring.IsByRefLike)
        {
            why = "declaring type is a ref struct";
            return false;
        }

        if (declaring.IsValueType)
        {
            var shape = ValueShape.For(declaring);
            if (shape != null)
            {
                plan.Receiver = ReceiverStrategy.Faithful;
                plan.ReceiverShape = shape;
                return true;
            }

            if (!allowDegenerate)
            {
                why = "receiver struct is not primitive-only";
                return false;
            }

            plan.Receiver = ReceiverStrategy.ZeroedStruct;
            Degenerate(plan, "zeroed-receiver");
            return true;
        }

        if (!allowDegenerate)
        {
            why = "receiver is a class";
            return false;
        }

        // GetUninitializedObject si nu un constructor recuperat, dinadins. Un constructor este el insusi
        // cod recuperat si nemasurat: daca ar crapa, esecul lui s-ar raporta ca esec al metodei pe care
        // voiam sa o masuram, si recensamantul ar da vina pe metoda gresita. Aici obiectul vine pe zero
        // din alocator, deci tot ce se masoara este corpul cerut - cu pretul ca fiecare camp referinta
        // este null, ceea ce este exact ce trebuie sa scrie in raport.
        plan.Receiver = ReceiverStrategy.Uninitialised;
        Degenerate(plan, "uninitialised-receiver");
        return true;
    }

    private static bool PlanValue(CensusPlan plan, Type type, bool allowDegenerate, out ArgStrategy strategy, out ValueShape shape, out string why)
    {
        strategy = ArgStrategy.Faithful;
        shape = null;
        why = null;

        if (type == null)
        {
            why = "null parameter type";
            return false;
        }

        // Un pointer nu poate fi impachetat fara cod unsafe, iar un ref struct nu poate fi impachetat
        // deloc - reflectia refuza apelul inainte sa atinga corpul.
        if (type.IsPointer || type.IsFunctionPointer || type.IsByRef)
        {
            why = "pointer or nested byref";
            return false;
        }

        if (type.IsByRefLike)
        {
            why = "ref struct (" + type.Name + ")";
            return false;
        }

        if (type.ContainsGenericParameters)
        {
            why = "open generic (" + type.Name + ")";
            return false;
        }

        shape = ValueShape.For(type);
        if (shape != null)
        {
            strategy = ArgStrategy.Faithful;
            return true;
        }

        if (!allowDegenerate)
        {
            why = "not primitive-only, and degenerate arguments are disabled";
            return false;
        }

        if (type == typeof(string))
        {
            strategy = ArgStrategy.EmptyString;
            Degenerate(plan, "empty-string");
            return true;
        }

        if (type.IsArray)
        {
            var element = type.GetElementType();
            if (element == null || element.IsPointer || element.ContainsGenericParameters || element.IsByRefLike)
            {
                why = "array of an unconstructible element";
                return false;
            }

            strategy = ArgStrategy.EmptyArray;
            Degenerate(plan, "empty-array");
            return true;
        }

        if (type.IsValueType)
        {
            strategy = ArgStrategy.ZeroedStruct;
            Degenerate(plan, "zeroed-struct");
            return true;
        }

        strategy = ArgStrategy.NullReference;
        Degenerate(plan, "null-reference");
        return true;
    }

    // Construieste un set de argumente. fuzz=false da zerouri peste tot, ceea ce este intrarea cea mai
    // probabila sa treaca prin garzile unei metode; fuzz=true da valori din acelasi generator pe care il
    // foloseste calea de comparatie, ca un corp care iese devreme pe zero sa apuce totusi sa ruleze.
    public static bool TryBuild(CensusPlan plan, ref DeterministicRandom random, bool fuzz, out object receiver, out object[] args, out string failure)
    {
        receiver = null;
        args = null;
        failure = null;

        try
        {
            if (plan.Receiver == ReceiverStrategy.Faithful)
                receiver = Value(plan.ReceiverShape, ref random, fuzz);
            else if (plan.Receiver == ReceiverStrategy.ZeroedStruct)
                receiver = Activator.CreateInstance(plan.ReceiverType);
            else if (plan.Receiver == ReceiverStrategy.Uninitialised)
                receiver = RuntimeHelpers.GetUninitializedObject(plan.ReceiverType);

            args = new object[plan.Strategies.Length];
            for (var i = 0; i < args.Length; i++)
            {
                // Un T& primeste in cutie valoarea elementului: reflectia o scrie inapoi in acelasi
                // element al vectorului, si o citim doar daca vrem - recensamantul nu vrea.
                var strategy = plan.Strategies[i] == ArgStrategy.ByRefBox ? plan.ByRefInner[i] : plan.Strategies[i];
                if (strategy == ArgStrategy.Faithful)
                    args[i] = Value(plan.Shapes[i], ref random, fuzz);
                else if (strategy == ArgStrategy.EmptyString)
                    args[i] = "";
                else if (strategy == ArgStrategy.EmptyArray)
                    args[i] = EmptyArray(plan.ParameterTypes[i]);
                else if (strategy == ArgStrategy.ZeroedStruct)
                    args[i] = Activator.CreateInstance(plan.ParameterTypes[i]);
                else
                    args[i] = null;
            }

            return true;
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    private static object EmptyArray(Type arrayType)
    {
        var element = arrayType.GetElementType();
        var rank = arrayType.GetArrayRank();
        return rank <= 1 ? Array.CreateInstance(element, 0) : Array.CreateInstance(element, new int[rank]);
    }

    private static object Value(ValueShape shape, ref DeterministicRandom random, bool fuzz)
    {
        if (!fuzz)
            return Activator.CreateInstance(shape.Type);

        var kinds = new List<LeafKind>();
        Collect(shape, kinds);
        var leaves = new object[kinds.Count];
        for (var i = 0; i < kinds.Count; i++)
            leaves[i] = FuzzInputs.RandomValue(kinds[i], ref random);

        var next = 0;
        return shape.Materialise(leaves, ref next);
    }

    private static void Collect(ValueShape shape, List<LeafKind> into)
    {
        if (shape.IsLeaf)
        {
            into.Add(shape.Kind);
            return;
        }

        foreach (var child in shape.Children)
            Collect(child, into);
    }

    // Inchide o metoda generica peste un argument de tip ghicit. MakeGenericType/MakeGenericMethod
    // verifica ele insele constrangerile si arunca daca ghicitura nu le respecta, deci lista de mai sus
    // se poate incerca pe rand fara nicio analiza proprie a constrangerilor.
    private static bool TryClose(MethodBase method, CensusPlan plan, out MethodBase closed, out string why)
    {
        closed = null;
        why = null;
        var declaring = method.DeclaringType;

        foreach (var guess in GenericGuesses)
        {
            try
            {
                var onType = method;
                if (declaring.ContainsGenericParameters)
                {
                    var typeArgs = declaring.GetGenericArguments();
                    var filled = new Type[typeArgs.Length];
                    for (var i = 0; i < filled.Length; i++)
                        filled[i] = guess;

                    var closedType = declaring.GetGenericTypeDefinition().MakeGenericType(filled);

                    // Singurul mod de a regasi ACEEASI metoda pe tipul inchis. Cautarea dupa nume ar
                    // alege alta supraincarcare, si in build-ul recuperat supraincarcarile abunda.
                    onType = MethodBase.GetMethodFromHandle(method.MethodHandle, closedType.TypeHandle);
                    if (onType == null)
                        continue;
                }

                if (onType.ContainsGenericParameters && onType is MethodInfo info && info.IsGenericMethodDefinition)
                {
                    var methodArgs = info.GetGenericArguments();
                    var filled = new Type[methodArgs.Length];
                    for (var i = 0; i < filled.Length; i++)
                        filled[i] = guess;

                    onType = info.MakeGenericMethod(filled);
                }

                if (onType.ContainsGenericParameters)
                    continue;

                plan.GenericGuess = true;
                plan.GenericGuessDetail = guess.Name;
                Degenerate(plan, "generic-guess-" + guess.Name);
                closed = onType;
                return true;
            }
            catch (Exception ex)
            {
                why = "no usable instantiation: " + ex.GetType().Name;
            }
        }

        why ??= "no usable instantiation";
        return false;
    }

    private static void Degenerate(CensusPlan plan, string kind)
    {
        plan.Degenerate = true;
        if (!plan.Degeneracies.Contains(kind))
            plan.Degeneracies.Add(kind);
    }

    private static CensusPlan Refuse(CensusPlan plan, CensusOutcome outcome, string detail)
    {
        plan.Ok = false;
        plan.Refusal = outcome;
        plan.RefusalDetail = detail;
        return plan;
    }
}
