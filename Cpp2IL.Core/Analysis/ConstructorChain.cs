using System;
using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Inchide lantul de constructori: orice <c>.ctor</c> de instanta trebuie sa initializeze <c>this</c>
/// printr-un apel catre un constructor al propriului tip sau al bazei lui DIRECTE, inainte de primul
/// <c>ret</c>.
///
/// Regula nu este de stil. Pana la acel apel verificatorul tine <c>this</c> drept neinitializat, iar un
/// <c>ret</c> pe starea aceea inseamna ca metoda nu ruleaza deloc: JIT-ul o refuza cu
/// InvalidProgramException. Un tip al carui constructor este refuzat nu poate fi instantiat, deci nu se
/// pierde o metoda, ci tot ce atarna de tipul acela.
///
/// Codul nativ pierde lantul in doua feluri diferite, si masuratoarea pe ilverify le arata pe amandoua:
///
/// 1. Apelul lipseste cu totul. Corpul este doar atribuiri de campuri si <c>ret</c>, sau chiar doar
///    <c>ret</c> acolo unde metoda a iesit ciot fiindca nu avea cod nativ - atributele generate de
///    compilator sunt exact asta. 207 din cele 813 ThisUninitReturn sunt raportate la offset 0, adica pe
///    un corp care se inchide imediat.
///
/// 2. Apelul exista, dar tinteste alt stramos decat baza directa. il2cpp inlineaza constructorii triviali
///    din lant, asa ca un AIAsset : AssetBase : ScriptableObject cheama direct constructorul lui
///    ScriptableObject. ECMA-335 permite dintr-un <c>.ctor</c> doar tipul propriu sau baza imediata, deci
///    verificatorul refuza apelul - CallCtor, 114 aparitii, din care 88 la offset 0x1, adica exact forma
///    <c>ldarg.0; call ...::.ctor()</c>. Retintirea catre baza directa este pe deasupra si mai aproape de
///    adevar decat apelul gasit: constructorul sarit chiar rula in programul original.
///
/// Cele doua familii se suprapun pe 113 metode din 114, si impreuna acopera fiecare dintre cele 927 de
/// constatari ThisUninitReturn si CallCtor din raport - toate, fara exceptie, in metode <c>.ctor</c>.
/// </summary>
public static class ConstructorChain
{
    // Pornit implicit (CPP2IL_CTOR_CHAIN=0 il opreste).
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("CPP2IL_CTOR_CHAIN") != "0";

    public static void Ensure(MethodDefinition definition)
    {
        if (!Enabled || definition.CilMethodBody is not { } body)
            return;

        // Un `.cctor` este static, deci cade tot aici: nu are `this` de initializat.
        if (definition.IsStatic || definition.Name != ".ctor")
            return;

        // Un struct nu are lant de constructori - `this` este un pointer administrat catre un slot care
        // exista deja - si regula pe care o reparam aici nu i se aplica.
        if (definition.DeclaringType is not { } declaring || declaring.IsValueType)
            return;

        // Fara baza directa nu avem ce chema: System.Object insusi si interfetele ajung aici.
        if (declaring.BaseType is not { } baseType || definition.DeclaringModule is not { } module)
            return;

        var existing = ChainedCallIndex(body, out var duplicated);

        // Doua apeluri de constructor in acelasi corp inseamna ca nu putem sti care dintre ele era menit
        // sa inchida lantul, iar retintirea celui gresit ar initializa obiectul de doua ori. Cazul apare
        // doar cand CPP2IL_CTOR_NEWOBJ este oprit, si atunci lasam corpul asa cum este.
        if (duplicated)
            return;

        var target = existing >= 0
            ? (body.Instructions[existing].Operand as IMethodDescriptor)?.DeclaringType
            : null;

        // `: this(...)` - alt constructor al aceluiasi tip initializeaza `this`, deci lantul e deja inchis
        // si orice am adauga aici ar fi a doua initializare.
        if (target != null && target.FullName == declaring.FullName)
            return;

        // Apelul catre baza directa este chiar ce cere verificatorul.
        if (target != null && target.FullName == baseType.FullName)
            return;

        if (BaseConstructor(module, baseType) is not { } baseConstructor)
            return;

        if (existing >= 0)
        {
            // Retintim doar forma pe care o putem citi intreaga: `ldarg.0; call X::.ctor()`. Un apel cu
            // argumente si le-ar avea impinse de instructiunile dinainte, iar constructorul bazei pe care
            // l-am pune in loc nu le primeste - deci l-am lasa cu valori care nu sunt ale lui.
            if (body.Instructions[existing].Operand is not IMethodDescriptor { Signature.ParameterTypes.Count: 0 }
                || existing < 1
                || body.Instructions[existing - 1].OpCode != CilOpCodes.Ldarg_0)
                return;

            body.Instructions[existing].Operand = baseConstructor;
            return;
        }

        // La inceput, nu oriunde: `this` trebuie initializat inainte de orice `ret`, iar un corp recuperat
        // are si iesiri timpurii pe ramuri - EntityView se inchide pe un `if` pe care lifterul l-a pastrat
        // din verificarea de null a codului nativ. Pus pe pozitia 0, apelul precede orice iesire, oricate
        // ar fi ele. Etichetele de salt trimit catre obiectul instructiune, nu catre offset, deci inserarea
        // nu muta nicio tinta de branch.
        body.Instructions.InsertRange(0, new List<CilInstruction>
        {
            new(CilOpCodes.Ldarg_0),
            new(CilOpCodes.Call, baseConstructor),
        });

        // Perechea urca stiva la 1 si o lasa tot la 0. Un corp care era doar `ret` a declarat adancimea 0,
        // iar sub-declararea costa toata metoda - acelasi motiv pentru care exista SetMaxStack.
        if (body.MaxStack < 1)
            body.MaxStack = 1;
    }

    /// <summary>
    /// Pozitia apelului de constructor din corp, sau -1 daca nu exista niciunul. Cu CPP2IL_CTOR_NEWOBJ
    /// pornit - implicit - orice constructie care nu era un lant a devenit deja <c>newobj</c>, deci un
    /// <c>call</c> catre <c>.ctor</c> ramas in corp este chiar `: base(...)` sau `: this(...)`.
    /// </summary>
    private static int ChainedCallIndex(CilMethodBody body, out bool duplicated)
    {
        var found = -1;
        duplicated = false;

        for (var i = 0; i < body.Instructions.Count; i++)
        {
            if (body.Instructions[i].OpCode != CilOpCodes.Call
                || body.Instructions[i].Operand is not IMethodDescriptor { Name.Value: ".ctor" })
                continue;

            if (found >= 0)
            {
                duplicated = true;
                return found;
            }

            found = i;
        }

        return found;
    }

    /// <summary>
    /// Constructorul fara parametri al bazei directe, ca referinta pe care modulul curent o poate purta -
    /// sau nimic acolo unde baza nu ofera unul. Nu inventam argumente: un constructor cu parametri chemat
    /// cu valori implicite ar da un obiect construit altfel decat il construia jocul, adica un corp care
    /// ruleaza si minte, mai rau decat unul pe care runtime-ul il refuza.
    ///
    /// Tot aici cad de la sine delegatii: baza lor, MulticastDelegate, nu are constructor fara parametri,
    /// deci nu le atingem.
    /// </summary>
    private static IMethodDescriptor? BaseConstructor(ModuleDefinition module, ITypeDefOrRef baseType)
    {
        if (Resolve(module, baseType) is not { } definition)
            return null;

        var parameterless = definition.Methods.FirstOrDefault(method =>
            method is { IsStatic: false, IsPrivate: false, Name.Value: ".ctor", Signature.ParameterTypes.Count: 0 });

        if (parameterless == null)
            return null;

        // O definitie din acelasi modul se poate chema direct.
        if (baseType is TypeDefinition && ReferenceEquals(parameterless.DeclaringModule, module))
            return parameterless;

        // Altfel referinta trebuie sa poarte exact tipul scris in metadata drept baza, cu tot cu
        // argumentele generice: pentru `Foo : Bar<int>`, definitia rezolvata este `Bar<T>` deschis, iar un
        // apel catre ea ar numi alt tip decat baza lui Foo. Parintele se ia pe tipul concret, fiindca doar
        // acestea trei pot purta o referinta de membru.
        var signature = MethodSignature.CreateInstance(module.CorLibTypeFactory.Void);

        return baseType switch
        {
            TypeDefinition parent => parent.CreateMemberReference(".ctor", signature),
            TypeReference parent => parent.CreateMemberReference(".ctor", signature),
            TypeSpecification parent => parent.CreateMemberReference(".ctor", signature),
            _ => null,
        };
    }

    // Un tip dintr-un assembly care nu e incarcat, sau o referinta pentru care nu se gaseste nimic, nu
    // rezolva deloc - si in unele forme arunca in loc sa intoarca null.
    private static TypeDefinition? Resolve(ModuleDefinition module, ITypeDefOrRef type)
    {
        try
        {
            return type.Resolve(module.RuntimeContext);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
