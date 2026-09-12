using AsmResolver.DotNet;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Metadata.Tables;
using AssetRipper.CIL;
using AssetRipper.Import.Structure.Assembly;
using AssetRipper.Import.Structure.Assembly.Managers;

namespace AssetRipper.Processing.Assemblies;

/// <summary>
/// Recover unmanaged constraints for generic parameters in Il2Cpp games. This prevents compile errors when using pointers to generic parameters.
/// </summary>
public sealed class UnmanagedConstraintRecoveryProcessor : IAssetProcessor
{
	public void Process(GameData gameData) => Process(gameData.AssemblyManager);

	private static void Process(IAssemblyManager manager)
	{
		if (manager.ScriptingBackend != ScriptingBackend.IL2Cpp)
		{
			return;
		}

		ModuleDefinition? mscorlib = manager.Mscorlib?.ManifestModule;
		if (mscorlib is null)
		{
			return;
		}

		if (!mscorlib.TryGetTopLevelType("System.Runtime.CompilerServices", "IsUnmanagedAttribute", out TypeDefinition? unmanagedAttributeType))
		{
			return;
		}

		MethodDefinition? unmanagedAttributeConstructor = unmanagedAttributeType.Methods.FirstOrDefault(m => m.IsConstructor && m.Parameters.Count == 0);
		if (unmanagedAttributeConstructor is null)
		{
			return;
		}

		// Nu atributul face un parametru unmanaged, ci codarea constrangerii, si fara aceste doua tipuri
		// nu o putem scrie. Iesim de tot in loc sa marcam parametri pe care nu ii putem termina; vezi
		// MakeUnmanaged pentru ce forma pe jumatate e mai rea decat sa nu facem nimic.
		if (!mscorlib.TryGetTopLevelType("System", "ValueType", out TypeDefinition? valueType))
		{
			return;
		}

		if (!mscorlib.TryGetTopLevelType("System.Runtime.InteropServices", "UnmanagedType", out TypeDefinition? unmanagedType))
		{
			return;
		}

		manager.ClearStreamCache();

		foreach (MethodDefinition method in manager.GetAllMethods())
		{
			foreach (TypeSignature? parameterSignature in method.Parameters.Select(p => p.ParameterType).Append(method.Signature?.ReturnType))
			{
				if (!IsPointerToGenericParameter(parameterSignature, out GenericParameterSignature? genericParameterSignature))
				{
				}
				else if (genericParameterSignature.ParameterType is GenericParameterType.Method)
				{
					GenericParameter genericParameter = method.GenericParameters[genericParameterSignature.Index];
					MakeUnmanaged(genericParameter, unmanagedAttributeConstructor, valueType, unmanagedType);
				}
				else
				{
					TypeDefinition? declaringType = method.DeclaringType;
					while (declaringType is not null && declaringType.GenericParameters.Count > genericParameterSignature.Index)
					{
						GenericParameter genericParameter = declaringType.GenericParameters[genericParameterSignature.Index];
						MakeUnmanaged(genericParameter, unmanagedAttributeConstructor, valueType, unmanagedType);

						declaringType = declaringType.DeclaringType;
					}
				}
			}
		}
	}

	static bool IsPointerToGenericParameter(TypeSignature? type, [NotNullWhen(true)] out GenericParameterSignature? genericParameter)
	{
		genericParameter = (type as PointerTypeSignature)?.BaseType as GenericParameterSignature;
		return genericParameter != null;
	}

	/// <summary>
	/// Un parametru generic e <c>unmanaged</c> in metadate numai cand trei lucruri sunt adevarate in
	/// acelasi timp: flagurile <c>valuetype</c> si <c>.ctor</c> sunt puse, exista o constrangere pe
	/// <c>System.ValueType</c>, iar constrangerea aia poarta
	/// <c>modreq(System.Runtime.InteropServices.UnmanagedType)</c>. Dezasamblarea a ce emite Roslyn
	/// arata diferenta exact:
	/// <code>
	/// where T : struct    -> valuetype .ctor ([netstandard]System.ValueType) T
	/// where T : unmanaged -> valuetype .ctor (class [netstandard]System.ValueType
	///                         modreq([netstandard]System.Runtime.InteropServices.UnmanagedType)) T
	/// </code>
	/// Randul simplu pe <c>System.ValueType</c> se emite si pentru <c>struct</c> - numai modreq-ul face
	/// constrangerea <c>unmanaged</c>.
	///
	/// Procesorul asta adauga inainte doar IsUnmanagedAttribute si nu atingea constrangerea. Roslyn
	/// citeste atributul, se asteapta la codarea de mai sus, nu o gaseste, si marcheaza parametrul ca
	/// metadata nesuportata - deci FIECARE folosire a tipului care il detine cade cu CS0570
	/// "'T' is not supported by the language", nu doar cele cu pointeri. Masurat pe un joc:
	/// <c>Quantum.Collections.QList`1</c> avea atributul si fiecare folosire a lui cadea, iar
	/// <c>QListPtr`1</c> avea flaguri identice si lista de constrangeri identica, dar niciun atribut, si
	/// compila curat. Atributul singur era toata diferenta.
	///
	/// Deci acum scriem codarea completa, iar apelantul refuza sa porneasca daca vreunul din cele doua
	/// tipuri lipseste din mscorlib. Regula e: un parametru ori primeste forma <c>unmanaged</c> intreaga,
	/// ori ramane exact cum era. Forma pe jumatate e singurul lucru care nu are voie sa iasa de aici,
	/// fiindca e strict mai rea decat daca procesorul nu ar rula deloc: transforma o constrangere care
	/// lipseste, si care costa un CS0208 numai unde chiar se scrie un pointer, intr-un CS0570 pe fiecare
	/// folosire a tipului.
	///
	/// Nu scriem niciodata un flag, ci numai un rand de constrangere si atributul. Asa un parametru care
	/// era <c>struct</c> ramane tiparibil ca <c>struct</c> de orice decompilator care citeste flaguri, iar
	/// unul care nu avea nicio constrangere nu capata una.
	/// </summary>
	static void MakeUnmanaged(GenericParameter genericParameter, MethodDefinition constructor, TypeDefinition valueType, TypeDefinition unmanagedType)
	{
		// Ridicam numai un parametru pe care metadatele il numesc deja tip valoare, si nu punem noi
		// niciodata flagul ala. il2cpp pastreaza flagurile: un parametru care chiar era `unmanaged` in
		// sursa originala ajunge aici cu `valuetype .ctor` intact - masurat, QList`1::T e 0x18. Deci cand
		// flagul lipseste, pointerul pe care l-am vazut in semnatura nu e sustinut de nimic si e mult mai
		// probabil un artefact al recuperarii de semnaturi decat un `T*` adevarat din sursa originala. Sa
		// inventam acolo o constrangere de tip valoare ar restrange un parametru care nu avea niciuna, si
		// orice instantiere cu un tip referinta ar incepe sa cada. Il lasam complet in pace in schimb -
		// fara flaguri, fara constrangere si fara atribut, deci fara nicio forma pe jumatate.
		//
		// E mai ingust decat facea procesorul inainte, si intentionat: comportamentul vechi marca si
		// parametrii aia, dar un atribut singur nu a produs niciodata un `unmanaged` care sa functioneze.
		if ((genericParameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) == 0)
		{
			return;
		}

		// `class` si `struct` se exclud reciproc. Metadatele care pretind amandoua sunt deja stricate si
		// nu punem si a treia pretentie peste ele.
		if ((genericParameter.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
		{
			return;
		}

		ModuleDefinition module = genericParameter.DeclaringModule!;

		if (!HasUnmanagedConstraint(genericParameter))
		{
			// Un rand simplu pe System.ValueType e scrierea `struct` a aceleiasi constrangeri. Trebuie
			// scos, altfel parametrul ar purta amandoua randurile si ar spune doua lucruri deodata.
			for (int i = genericParameter.Constraints.Count - 1; i >= 0; i--)
			{
				if (IsPlainValueType(genericParameter.Constraints[i].Constraint))
				{
					genericParameter.Constraints.RemoveAt(i);
				}
			}

			TypeSignature constraintSignature = new CustomModifierTypeSignature(
				module.DefaultImporter.ImportType(unmanagedType),
				true,
				module.DefaultImporter.ImportType(valueType).ToTypeSignature(false));

			genericParameter.Constraints.Add(new GenericParameterConstraint(constraintSignature.ToTypeDefOrRef()));
		}

		if (!genericParameter.HasCustomAttribute("System.Runtime.CompilerServices", "IsUnmanagedAttribute"))
		{
			genericParameter.AddCustomAttribute(module.DefaultImporter.ImportMethod(constructor));
		}
	}

	static bool HasUnmanagedConstraint(GenericParameter genericParameter)
	{
		foreach (GenericParameterConstraint constraint in genericParameter.Constraints)
		{
			if (constraint.Constraint is TypeSpecification { Signature: CustomModifierTypeSignature { IsRequired: true, ModifierType: { } modifierType } }
				&& modifierType.IsTypeOf("System.Runtime.InteropServices", "UnmanagedType"))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>
	/// O constrangere pe System.ValueType fara niciun modificator pe ea - forma `struct`. TypeSpecification
	/// e exclus intentionat: acolo sta o constrangere cu modificator, iar despre alea a decis deja
	/// HasUnmanagedConstraint.
	/// </summary>
	static bool IsPlainValueType(ITypeDefOrRef? constraint)
	{
		return constraint is not null
			&& constraint is not TypeSpecification
			&& constraint.IsTypeOf("System", "ValueType");
	}
}
